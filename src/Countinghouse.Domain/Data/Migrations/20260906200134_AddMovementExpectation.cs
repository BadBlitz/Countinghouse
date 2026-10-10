using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Countinghouse.Domain.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMovementExpectation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ExpectedMovement",
                table: "Accounts",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ExpectedMovementDays",
                table: "Accounts",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExpectedMovement",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "ExpectedMovementDays",
                table: "Accounts");
        }
    }
}
