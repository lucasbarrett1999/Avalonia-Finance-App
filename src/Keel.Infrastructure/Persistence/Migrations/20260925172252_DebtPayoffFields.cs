using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Keel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DebtPayoffFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "InterestRateBps",
                table: "Accounts",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "MinimumPayment",
                table: "Accounts",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "InterestRateBps",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "MinimumPayment",
                table: "Accounts");
        }
    }
}
