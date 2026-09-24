using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Keel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ImportMatchFlag : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "HasImportMatch",
                table: "Transactions",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HasImportMatch",
                table: "Transactions");
        }
    }
}
