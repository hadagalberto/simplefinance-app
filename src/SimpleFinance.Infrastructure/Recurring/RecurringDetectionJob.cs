using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Persistence;

namespace SimpleFinance.Infrastructure.Recurring;

/// <summary>
/// RECUR-01/RECUR-04: the SOLE writer of RecurringTransaction rows -- a thin Hangfire-invoked
/// adapter (mirrors NetWorthSnapshotJob's exact constructor-injection/ExecuteAsync shape) that
/// iterates every User and, per user, queries POSTED/non-transfer/non-hidden/Debit Transactions
/// within a rolling LookbackMonths window, feeds them to RecurringDetector.DetectGroups (07-02,
/// pure), and idempotently upserts one RecurringTransaction row per (UserId, NormalizedMerchant)
/// -- re-running never duplicates, always updates the existing row in place. Also invoked once at
/// boot by RecurringDetectionStartupTask so the recurring panel isn't empty at boot.
///
/// Deliberate simplicity choice: each run's FirstSeenDate/LastSeenDate/OccurrenceCount always
/// reflect ONLY the current rolling window's detection result, never accumulated/merged across
/// runs. This keeps the upsert a pure "overwrite with this run's fresh computation" (aside from
/// PreviousAverageAmount/IsNewlyDetected, which explicitly carry state across runs) -- not a bug.
///
/// Mirrors NetWorthSnapshotJob's per-user isolation pattern verbatim (T-06-06 precedent): one
/// user's compute-and-upsert failure is caught, logged with structured UserId context, and never
/// aborts the batch -- every other user must still get their recurring rows written.
/// SaveChangesAsync is called once per user (never batched across the whole loop) so one user's
/// failure can't roll back another's already-saved rows; the catch block also calls
/// _db.ChangeTracker.Clear() so a failed user's un-saved entities can never leak into and re-fail
/// the NEXT user's SaveChangesAsync call.
/// </summary>
public sealed class RecurringDetectionJob
{
    private readonly SimpleFinanceDbContext _db;
    private readonly ILogger<RecurringDetectionJob> _logger;
    private readonly RecurringOptions _options;

    public RecurringDetectionJob(
        SimpleFinanceDbContext db, ILogger<RecurringDetectionJob> logger, IOptions<RecurringOptions> options)
    {
        _db = db;
        _logger = logger;
        _options = options.Value;
    }

    public async Task ExecuteAsync(CancellationToken ct)
    {
        var users = await _db.Users.ToListAsync(ct);
        var cutoff = DateTime.UtcNow.AddMonths(-_options.LookbackMonths);

        var detectionOptions = new RecurringDetectionOptions(
            _options.MinOccurrences,
            _options.AmountTolerancePercent,
            _options.WeeklyToleranceDays,
            _options.MonthlyMinDays,
            _options.MonthlyMaxDays,
            _options.YearlyToleranceDays);

        foreach (var user in users)
        {
            try
            {
                // Same POSTED/non-transfer/non-hidden exclusion discipline DashboardQueries.cs
                // documents for every other aggregate in this codebase, plus Debit-only (RECUR-01
                // targets expense subscriptions/bills, not recurring income).
                var transactions = await _db.Transactions
                    .Where(t => t.UserId == user.Id
                        && t.Status == TransactionStatus.Posted
                        && !t.IsTransfer
                        && !t.IsHidden
                        && t.Type == TransactionType.Debit
                        && t.Date >= cutoff)
                    .ToListAsync(ct);

                var inputs = transactions
                    .Select(t => new RecurringTransactionInput(
                        t.Id, t.Date, Math.Abs(t.Amount), t.MerchantName, t.Description, t.CategoryId))
                    .ToList();

                var groups = RecurringDetector.DetectGroups(inputs, detectionOptions);

                foreach (var group in groups)
                {
                    var existing = await _db.RecurringTransactions.FirstOrDefaultAsync(
                        r => r.UserId == user.Id && r.NormalizedMerchant == group.NormalizedMerchant, ct);

                    RecurringTransaction recurring;

                    if (existing is null)
                    {
                        recurring = new RecurringTransaction
                        {
                            Id = Guid.NewGuid(),
                            UserId = user.Id,
                            NormalizedMerchant = group.NormalizedMerchant,
                            DisplayName = group.DisplayName,
                            CategoryId = group.CategoryId,
                            AverageAmount = group.AverageAmount,
                            PreviousAverageAmount = null,
                            Frequency = group.Frequency,
                            FirstSeenDate = group.FirstSeenDate,
                            LastSeenDate = group.LastSeenDate,
                            NextExpectedDate = group.NextExpectedDate,
                            OccurrenceCount = group.OccurrenceCount,
                            IsSubscription = false,
                            IsActive = true,
                            IsNewlyDetected = true,
                            AmountIncreaseDetected = false,
                            CreatedAt = DateTime.UtcNow,
                            UpdatedAt = DateTime.UtcNow,
                        };
                        _db.RecurringTransactions.Add(recurring);
                    }
                    else
                    {
                        // Capture BEFORE overwriting -- this is the "prior stored average" RECUR-04's
                        // amount-increase comparison and PreviousAverageAmount both need.
                        var previousAverage = existing.AverageAmount;

                        existing.DisplayName = group.DisplayName;
                        existing.CategoryId = group.CategoryId;
                        existing.AverageAmount = group.AverageAmount;
                        existing.PreviousAverageAmount = previousAverage;
                        existing.Frequency = group.Frequency;
                        existing.FirstSeenDate = group.FirstSeenDate;
                        existing.LastSeenDate = group.LastSeenDate;
                        existing.NextExpectedDate = group.NextExpectedDate;
                        existing.OccurrenceCount = group.OccurrenceCount;
                        existing.IsNewlyDetected = false;
                        existing.AmountIncreaseDetected = previousAverage != 0
                            && (group.AverageAmount - previousAverage) / previousAverage > _options.AmountIncreaseThresholdPercent;
                        existing.UpdatedAt = DateTime.UtcNow;

                        recurring = existing;
                    }

                    // A fresh Add already has its Id (Guid.NewGuid() assigned above) -- no
                    // SaveChanges round-trip needed before tagging member transactions. Simple
                    // foreach over the already-loaded/tracked list -- no bulk-SQL library needed
                    // at this data scale (T-07-05 accepted risk).
                    var memberIds = group.MemberTransactionIds;
                    foreach (var txn in transactions.Where(t => memberIds.Contains(t.Id)))
                    {
                        txn.RecurringTransactionId = recurring.Id;
                    }
                }

                await _db.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                // T-06-06 precedent applied here: never let one user's failure abort the batch --
                // log structured context (UserId included) and move on. ChangeTracker.Clear()
                // discards this user's un-saved (Added/Modified) entities so they can never leak
                // into and re-fail the NEXT user's SaveChangesAsync call.
                _logger.LogError(ex, "RecurringDetectionJob failed to detect recurring transactions for user {UserId}", user.Id);
                _db.ChangeTracker.Clear();
            }
        }
    }
}
