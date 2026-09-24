using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Keel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RegisterIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Transactions_Register_Account",
                table: "Transactions",
                columns: new[] { "AccountId", "Date", "Id", "IsDeleted", "Amount", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_Register_All",
                table: "Transactions",
                columns: new[] { "Date", "Id", "IsDeleted", "Amount", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Transactions_Register_Account",
                table: "Transactions");

            migrationBuilder.DropIndex(
                name: "IX_Transactions_Register_All",
                table: "Transactions");
        }
    }
}
