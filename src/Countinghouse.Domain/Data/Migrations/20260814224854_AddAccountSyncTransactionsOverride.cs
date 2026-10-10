using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Countinghouse.Domain.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAccountSyncTransactionsOverride : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "SyncTransactionsOverride",
                table: "Accounts",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SyncTransactionsOverride",
                table: "Accounts");
        }
    }
}
