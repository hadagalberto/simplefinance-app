using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimpleFinance.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddConnectionCustomName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CustomName",
                table: "Connections",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CustomName",
                table: "Connections");
        }
    }
}
