using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyApp.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPlaidItemEntity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PlaidItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ItemId = table.Column<string>(type: "TEXT", nullable: false),
                    AccessToken = table.Column<string>(type: "TEXT", nullable: false),
                    InstitutionId = table.Column<string>(type: "TEXT", nullable: true),
                    InstitutionName = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastAccessedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    NextRefreshScheduledAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    HasError = table.Column<bool>(type: "INTEGER", nullable: false),
                    ErrorCode = table.Column<string>(type: "TEXT", nullable: true),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                    AvailableProducts = table.Column<string>(type: "TEXT", nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlaidItems", x => x.Id);
                    table.UniqueConstraint("AK_PlaidItems_ItemId", x => x.ItemId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Accounts_PlaidItemId",
                table: "Accounts",
                column: "PlaidItemId");

            migrationBuilder.CreateIndex(
                name: "IX_PlaidItems_ItemId",
                table: "PlaidItems",
                column: "ItemId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Accounts_PlaidItems_PlaidItemId",
                table: "Accounts",
                column: "PlaidItemId",
                principalTable: "PlaidItems",
                principalColumn: "ItemId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Accounts_PlaidItems_PlaidItemId",
                table: "Accounts");

            migrationBuilder.DropTable(
                name: "PlaidItems");

            migrationBuilder.DropIndex(
                name: "IX_Accounts_PlaidItemId",
                table: "Accounts");
        }
    }
}
