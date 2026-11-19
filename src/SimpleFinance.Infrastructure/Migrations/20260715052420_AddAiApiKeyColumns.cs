using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimpleFinance.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAiApiKeyColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DeepSeekApiKeyEncrypted",
                table: "UserSettings",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OpenAiApiKeyEncrypted",
                table: "UserSettings",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeepSeekApiKeyEncrypted",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "OpenAiApiKeyEncrypted",
                table: "UserSettings");
        }
    }
}
