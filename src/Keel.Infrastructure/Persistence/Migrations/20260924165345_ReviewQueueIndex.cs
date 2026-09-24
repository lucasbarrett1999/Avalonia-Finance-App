using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Keel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReviewQueueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Transactions_ReviewQueue",
                table: "Transactions",
                columns: new[] { "IsApproved", "Date", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Transactions_ReviewQueue",
                table: "Transactions");
        }
    }
}
