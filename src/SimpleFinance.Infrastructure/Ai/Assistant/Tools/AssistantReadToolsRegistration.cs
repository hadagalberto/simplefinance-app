using Microsoft.Extensions.DependencyInjection;
using SimpleFinance.Application.Ai.Assistant;
using SimpleFinance.Infrastructure.Budgets;
using SimpleFinance.Infrastructure.Investments;
using SimpleFinance.Infrastructure.NetWorth;
using SimpleFinance.Infrastructure.Reporting;

namespace SimpleFinance.Infrastructure.Ai.Assistant.Tools;

/// <summary>
/// AI-CHAT-02: DI registration for the seven UserId-scoped read tools and the narrow reporting-service
/// bridge interfaces they depend on. Kept in its own file (called from <c>AddAiClients</c>) so the tool
/// catalog grows here as later waves add tools, without churning the main AI composition block. Each
/// tool is registered as an <see cref="IAssistantTool"/> so the <see cref="ToolCatalog"/> and
/// <see cref="ToolDispatcher"/> enumerate them. The bridges expose the already-registered concrete
/// reporting services (DashboardQueries in AddReporting; BudgetService/NetWorthService/InvestmentsService
/// in Program.cs) behind a narrow interface the tools depend on — reusing the real queries while keeping
/// each tool unit-testable with a fake.
/// </summary>
public static class AssistantReadToolsRegistration
{
    public static IServiceCollection AddAssistantReadTools(this IServiceCollection services)
    {
        // Bridges: narrow read interfaces resolving to the concrete reporting services already
        // registered elsewhere. Scoped is safe — resolving a Singleton (DashboardQueries) from a Scoped
        // factory is fine, and BudgetService/NetWorthService/InvestmentsService are themselves Scoped.
        services.AddScoped<ICategorySpendReader>(sp => sp.GetRequiredService<DashboardQueries>());
        services.AddScoped<IPeriodTotalsReader>(sp => sp.GetRequiredService<DashboardQueries>());
        services.AddScoped<IBudgetReader>(sp => sp.GetRequiredService<BudgetService>());
        services.AddScoped<INetWorthReader>(sp => sp.GetRequiredService<NetWorthService>());
        services.AddScoped<IInvestmentsReader>(sp => sp.GetRequiredService<InvestmentsService>());
        services.AddScoped<ITopMerchantsReader>(sp => sp.GetRequiredService<ReportQueries>());

        // The seven read tools — each Scoped (they touch the DbContext / Scoped reporting services) and
        // registered against IAssistantTool so the catalog + dispatcher discover them by enumeration.
        services.AddScoped<IAssistantTool, SumByCategoryTool>();
        services.AddScoped<IAssistantTool, QueryTransactionsTool>();
        services.AddScoped<IAssistantTool, BudgetStatusTool>();
        services.AddScoped<IAssistantTool, NetWorthTool>();
        services.AddScoped<IAssistantTool, NetWorthEvolutionTool>();
        services.AddScoped<IAssistantTool, InvestmentsSummaryTool>();
        services.AddScoped<IAssistantTool, ListRecurringTool>();

        // AI-CHAT-03 RAG tool: semantic transaction search (embed query → pgvector kNN over the user's
        // TransactionEmbedding index). Registered against the SAME IAssistantTool contract so the catalog
        // + dispatcher pick it up by enumeration — no dispatcher change. Depends on IEmbeddingClient +
        // the pgvector index (Phase 16); degrades to "indisponível" when embeddings are unavailable.
        services.AddScoped<IAssistantTool, SemanticSearchTool>();

        // AI-CHAT-04 write tools (IsWrite=true) — each stages an AiPendingAction(Pending) via
        // AiPendingActionService and returns a confirmation-card descriptor instead of mutating. Registered
        // against the SAME IAssistantTool contract so the catalog + dispatcher enumerate them with no
        // change; the orchestrator's IsWrite path turns their result into an `action` SSE frame.
        services.AddScoped<IAssistantTool, CreateCategoryRuleTool>();
        services.AddScoped<IAssistantTool, SetBudgetTool>();
        services.AddScoped<IAssistantTool, CreateGoalTool>();
        services.AddScoped<IAssistantTool, RecategorizeTransactionsTool>();

        return services;
    }
}
