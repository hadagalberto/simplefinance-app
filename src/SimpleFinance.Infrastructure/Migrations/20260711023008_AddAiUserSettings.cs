using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimpleFinance.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAiUserSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AiAnomalyEnabled",
                table: "UserSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "AiAssistantEnabled",
                table: "UserSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<double>(
                name: "AiAutoApplyThreshold",
                table: "UserSettings",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<bool>(
                name: "AiCategorizationEnabled",
                table: "UserSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "AiChatModel",
                table: "UserSettings",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "AiEnabled",
                table: "UserSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "AiForecastEnabled",
                table: "UserSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "AiInsightsEnabled",
                table: "UserSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "AiLanguage",
                table: "UserSettings",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<decimal>(
                name: "AiMonthlyBudgetUsd",
                table: "UserSettings",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<bool>(
                name: "AiMonthlySummaryEnabled",
                table: "UserSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "AiSummaryModel",
                table: "UserSettings",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AiAnomalyEnabled",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "AiAssistantEnabled",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "AiAutoApplyThreshold",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "AiCategorizationEnabled",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "AiChatModel",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "AiEnabled",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "AiForecastEnabled",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "AiInsightsEnabled",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "AiLanguage",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "AiMonthlyBudgetUsd",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "AiMonthlySummaryEnabled",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "AiSummaryModel",
                table: "UserSettings");
        }
    }
}
