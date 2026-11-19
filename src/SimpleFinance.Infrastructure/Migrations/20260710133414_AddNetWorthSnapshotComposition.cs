using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimpleFinance.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddNetWorthSnapshotComposition : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "AccountsBalance",
                table: "NetWorthSnapshots",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "CreditCardDebt",
                table: "NetWorthSnapshots",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "InvestmentsBalance",
                table: "NetWorthSnapshots",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AccountsBalance",
                table: "NetWorthSnapshots");

            migrationBuilder.DropColumn(
                name: "CreditCardDebt",
                table: "NetWorthSnapshots");

            migrationBuilder.DropColumn(
                name: "InvestmentsBalance",
                table: "NetWorthSnapshots");
        }
    }
}
