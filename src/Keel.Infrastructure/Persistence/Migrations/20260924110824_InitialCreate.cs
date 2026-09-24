using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Keel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuditEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    At = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    EntityType = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    EntityId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    BeforeJson = table.Column<string>(type: "TEXT", nullable: true),
                    AfterJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CategoryGroups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    IsSystem = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsHidden = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CategoryGroups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Profiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    IsDefault = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Profiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Rules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    ContinueAfterMatch = table.Column<bool>(type: "INTEGER", nullable: false),
                    ConditionsJson = table.Column<string>(type: "TEXT", nullable: false),
                    ActionsJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Rules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Settings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    ValueJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Settings", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "SyncConnections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Provider = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    InstitutionName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    ExternalItemId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Cursor = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    LastSyncAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    SecretRef = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncConnections", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Tags",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tags", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Accounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Type = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Currency = table.Column<string>(type: "TEXT", maxLength: 3, nullable: false),
                    IsOnBudget = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsClosed = table.Column<bool>(type: "INTEGER", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    OpeningDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    OwnerProfileId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SyncConnectionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ProviderAccountId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    ReportedBalance = table.Column<long>(type: "INTEGER", nullable: true),
                    ReportedBalanceAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Accounts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Accounts_Profiles_OwnerProfileId",
                        column: x => x.OwnerProfileId,
                        principalTable: "Profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Accounts_SyncConnections_SyncConnectionId",
                        column: x => x.SyncConnectionId,
                        principalTable: "SyncConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "BalanceSnapshots",
                columns: table => new
                {
                    AccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Balance = table.Column<long>(type: "INTEGER", nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BalanceSnapshots", x => new { x.AccountId, x.Date });
                    table.ForeignKey(
                        name: "FK_BalanceSnapshots_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Categories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    GroupId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    IsSystem = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsHidden = table.Column<bool>(type: "INTEGER", nullable: false),
                    LinkedAccountId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    FlexKind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Categories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Categories_Accounts_LinkedAccountId",
                        column: x => x.LinkedAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Categories_CategoryGroups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "CategoryGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Reconciliations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    StatementDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    StatementBalance = table.Column<long>(type: "INTEGER", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Reconciliations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Reconciliations_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BudgetAssignments",
                columns: table => new
                {
                    CategoryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Month = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Assigned = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BudgetAssignments", x => new { x.CategoryId, x.Month });
                    table.ForeignKey(
                        name: "FK_BudgetAssignments_Categories_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "Categories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Payees",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    NormalizedName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    DefaultCategoryId = table.Column<Guid>(type: "TEXT", nullable: true),
                    IsTransferPayeeForAccountId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Payees", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Payees_Accounts_IsTransferPayeeForAccountId",
                        column: x => x.IsTransferPayeeForAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Payees_Categories_DefaultCategoryId",
                        column: x => x.DefaultCategoryId,
                        principalTable: "Categories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "Targets",
                columns: table => new
                {
                    CategoryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Type = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Amount = table.Column<long>(type: "INTEGER", nullable: false),
                    TargetDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    Cadence = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    LinkedAccountId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Targets", x => x.CategoryId);
                    table.ForeignKey(
                        name: "FK_Targets_Accounts_LinkedAccountId",
                        column: x => x.LinkedAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Targets_Categories_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "Categories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ScheduledTransactions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Amount = table.Column<long>(type: "INTEGER", nullable: false),
                    PayeeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CategoryId = table.Column<Guid>(type: "TEXT", nullable: true),
                    TransferAccountId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Memo = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    RecurrenceRule = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    NextDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    EndDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    AutoEnter = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduledTransactions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ScheduledTransactions_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ScheduledTransactions_Accounts_TransferAccountId",
                        column: x => x.TransferAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ScheduledTransactions_Categories_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "Categories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ScheduledTransactions_Payees_PayeeId",
                        column: x => x.PayeeId,
                        principalTable: "Payees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RecurringItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PayeeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AccountId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Cadence = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ExpectedAmount = table.Column<long>(type: "INTEGER", nullable: false),
                    AmountTolerance = table.Column<long>(type: "INTEGER", nullable: false),
                    IsVariableAmount = table.Column<bool>(type: "INTEGER", nullable: false),
                    NextExpectedDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    LastSeenDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Confidence = table.Column<double>(type: "REAL", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    IsSubscription = table.Column<bool>(type: "INTEGER", nullable: false),
                    CategoryId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ScheduledTransactionId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecurringItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RecurringItems_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_RecurringItems_Categories_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "Categories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_RecurringItems_Payees_PayeeId",
                        column: x => x.PayeeId,
                        principalTable: "Payees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RecurringItems_ScheduledTransactions_ScheduledTransactionId",
                        column: x => x.ScheduledTransactionId,
                        principalTable: "ScheduledTransactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "Transactions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    PayeeId = table.Column<Guid>(type: "TEXT", nullable: true),
                    PayeeRaw = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Memo = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Amount = table.Column<long>(type: "INTEGER", nullable: false),
                    CategoryId = table.Column<Guid>(type: "TEXT", nullable: true),
                    TransferAccountId = table.Column<Guid>(type: "TEXT", nullable: true),
                    TransferPairId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    IsApproved = table.Column<bool>(type: "INTEGER", nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ProviderTransactionId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    ProviderPendingId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    ImportFingerprint = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ScheduledFromId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Transactions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Transactions_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Transactions_Accounts_TransferAccountId",
                        column: x => x.TransferAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Transactions_Categories_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "Categories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Transactions_Payees_PayeeId",
                        column: x => x.PayeeId,
                        principalTable: "Payees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Transactions_ScheduledTransactions_ScheduledFromId",
                        column: x => x.ScheduledFromId,
                        principalTable: "ScheduledTransactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "Alerts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    RecurringItemId = table.Column<Guid>(type: "TEXT", nullable: true),
                    TransactionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ReadAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DismissedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Alerts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Alerts_RecurringItems_RecurringItemId",
                        column: x => x.RecurringItemId,
                        principalTable: "RecurringItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Alerts_Transactions_TransactionId",
                        column: x => x.TransactionId,
                        principalTable: "Transactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "Attachments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TransactionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 260, nullable: false),
                    Sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    MimeType = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Attachments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Attachments_Transactions_TransactionId",
                        column: x => x.TransactionId,
                        principalTable: "Transactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TransactionSplits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TransactionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CategoryId = table.Column<Guid>(type: "TEXT", nullable: true),
                    TransferAccountId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Memo = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Amount = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TransactionSplits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TransactionSplits_Accounts_TransferAccountId",
                        column: x => x.TransferAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TransactionSplits_Categories_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "Categories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TransactionSplits_Transactions_TransactionId",
                        column: x => x.TransactionId,
                        principalTable: "Transactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TransactionTags",
                columns: table => new
                {
                    TransactionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TagId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TransactionTags", x => new { x.TransactionId, x.TagId });
                    table.ForeignKey(
                        name: "FK_TransactionTags_Tags_TagId",
                        column: x => x.TagId,
                        principalTable: "Tags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TransactionTags_Transactions_TransactionId",
                        column: x => x.TransactionId,
                        principalTable: "Transactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "CategoryGroups",
                columns: new[] { "Id", "IsHidden", "IsSystem", "Name", "SortOrder" },
                values: new object[,]
                {
                    { new Guid("00000000-0000-7000-8000-000000000010"), false, true, "Inflow", 0 },
                    { new Guid("00000000-0000-7000-8000-000000000011"), false, true, "Credit Card Payments", 1 }
                });

            migrationBuilder.InsertData(
                table: "Profiles",
                columns: new[] { "Id", "IsDefault", "Name" },
                values: new object[] { new Guid("00000000-0000-7000-8000-000000000001"), true, "Me" });

            migrationBuilder.InsertData(
                table: "Categories",
                columns: new[] { "Id", "FlexKind", "GroupId", "IsHidden", "IsSystem", "LinkedAccountId", "Name", "Notes", "SortOrder" },
                values: new object[] { new Guid("00000000-0000-7000-8000-000000000020"), "Unset", new Guid("00000000-0000-7000-8000-000000000010"), false, true, null, "Ready to Assign", null, 0 });

            migrationBuilder.CreateIndex(
                name: "IX_Accounts_OwnerProfileId",
                table: "Accounts",
                column: "OwnerProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_Accounts_SyncConnectionId_ProviderAccountId",
                table: "Accounts",
                columns: new[] { "SyncConnectionId", "ProviderAccountId" });

            migrationBuilder.CreateIndex(
                name: "IX_Alerts_CreatedAt",
                table: "Alerts",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Alerts_RecurringItemId",
                table: "Alerts",
                column: "RecurringItemId");

            migrationBuilder.CreateIndex(
                name: "IX_Alerts_TransactionId",
                table: "Alerts",
                column: "TransactionId");

            migrationBuilder.CreateIndex(
                name: "IX_Attachments_Sha256",
                table: "Attachments",
                column: "Sha256");

            migrationBuilder.CreateIndex(
                name: "IX_Attachments_TransactionId",
                table: "Attachments",
                column: "TransactionId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_At",
                table: "AuditEvents",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_EntityType_EntityId",
                table: "AuditEvents",
                columns: new[] { "EntityType", "EntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_BudgetAssignments_Month",
                table: "BudgetAssignments",
                column: "Month");

            migrationBuilder.CreateIndex(
                name: "IX_Categories_GroupId_SortOrder",
                table: "Categories",
                columns: new[] { "GroupId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_Categories_LinkedAccountId",
                table: "Categories",
                column: "LinkedAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_Payees_DefaultCategoryId",
                table: "Payees",
                column: "DefaultCategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_Payees_IsTransferPayeeForAccountId",
                table: "Payees",
                column: "IsTransferPayeeForAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_Payees_NormalizedName",
                table: "Payees",
                column: "NormalizedName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Reconciliations_AccountId_StatementDate",
                table: "Reconciliations",
                columns: new[] { "AccountId", "StatementDate" });

            migrationBuilder.CreateIndex(
                name: "IX_RecurringItems_AccountId",
                table: "RecurringItems",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_RecurringItems_CategoryId",
                table: "RecurringItems",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_RecurringItems_NextExpectedDate",
                table: "RecurringItems",
                column: "NextExpectedDate");

            migrationBuilder.CreateIndex(
                name: "IX_RecurringItems_PayeeId_AccountId",
                table: "RecurringItems",
                columns: new[] { "PayeeId", "AccountId" });

            migrationBuilder.CreateIndex(
                name: "IX_RecurringItems_ScheduledTransactionId",
                table: "RecurringItems",
                column: "ScheduledTransactionId");

            migrationBuilder.CreateIndex(
                name: "IX_Rules_SortOrder",
                table: "Rules",
                column: "SortOrder");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledTransactions_AccountId",
                table: "ScheduledTransactions",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledTransactions_CategoryId",
                table: "ScheduledTransactions",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledTransactions_NextDate",
                table: "ScheduledTransactions",
                column: "NextDate");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledTransactions_PayeeId",
                table: "ScheduledTransactions",
                column: "PayeeId");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledTransactions_TransferAccountId",
                table: "ScheduledTransactions",
                column: "TransferAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_Tags_Name",
                table: "Tags",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Targets_LinkedAccountId",
                table: "Targets",
                column: "LinkedAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_AccountId_Date",
                table: "Transactions",
                columns: new[] { "AccountId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_CategoryId_Date",
                table: "Transactions",
                columns: new[] { "CategoryId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_Date",
                table: "Transactions",
                column: "Date");

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_ImportFingerprint",
                table: "Transactions",
                column: "ImportFingerprint");

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_PayeeId",
                table: "Transactions",
                column: "PayeeId");

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_ProviderTransactionId",
                table: "Transactions",
                column: "ProviderTransactionId");

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_ScheduledFromId",
                table: "Transactions",
                column: "ScheduledFromId");

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_TransferAccountId",
                table: "Transactions",
                column: "TransferAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_TransferPairId",
                table: "Transactions",
                column: "TransferPairId");

            migrationBuilder.CreateIndex(
                name: "IX_TransactionSplits_CategoryId",
                table: "TransactionSplits",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_TransactionSplits_TransactionId",
                table: "TransactionSplits",
                column: "TransactionId");

            migrationBuilder.CreateIndex(
                name: "IX_TransactionSplits_TransferAccountId",
                table: "TransactionSplits",
                column: "TransferAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_TransactionTags_TagId",
                table: "TransactionTags",
                column: "TagId");
            // sum(splits) == parent amount, and a split parent has no category (PRD 6.2).
            // SQLite CHECK constraints cannot contain subqueries, and a per-row check would fail
            // while a split set is being written. Triggers therefore record violations in
            // SplitSumViolations, whose Guard column references an always-empty table through a
            // DEFERRABLE INITIALLY DEFERRED foreign key: any violation still present at COMMIT
            // fails the transaction. See docs/decisions/0003-split-sum-constraint-via-triggers.md.
            migrationBuilder.Sql(@"CREATE TABLE ""SplitSumGuard"" (""Id"" INTEGER NOT NULL PRIMARY KEY);");
            migrationBuilder.Sql(@"CREATE TABLE ""SplitSumViolations"" (
    ""TransactionId"" TEXT NOT NULL PRIMARY KEY,
    ""Guard"" INTEGER NOT NULL REFERENCES ""SplitSumGuard"" (""Id"") DEFERRABLE INITIALLY DEFERRED
);");

            const string recheck = @"
    DELETE FROM ""SplitSumViolations"" WHERE ""TransactionId"" = {0};
    INSERT INTO ""SplitSumViolations"" (""TransactionId"", ""Guard"")
    SELECT t.""Id"", 0 FROM ""Transactions"" AS t
    WHERE t.""Id"" = {0}
      AND EXISTS (SELECT 1 FROM ""TransactionSplits"" AS s WHERE s.""TransactionId"" = t.""Id"")
      AND (t.""CategoryId"" IS NOT NULL
           OR t.""Amount"" <> (SELECT SUM(s.""Amount"") FROM ""TransactionSplits"" AS s WHERE s.""TransactionId"" = t.""Id""));";

            migrationBuilder.Sql(@"CREATE TRIGGER ""TR_TransactionSplits_Insert_SplitSum"" AFTER INSERT ON ""TransactionSplits""
BEGIN" + string.Format(System.Globalization.CultureInfo.InvariantCulture, recheck, @"NEW.""TransactionId""") + @"
END;");
            migrationBuilder.Sql(@"CREATE TRIGGER ""TR_TransactionSplits_Update_SplitSum"" AFTER UPDATE ON ""TransactionSplits""
BEGIN" + string.Format(System.Globalization.CultureInfo.InvariantCulture, recheck, @"OLD.""TransactionId""")
                + string.Format(System.Globalization.CultureInfo.InvariantCulture, recheck, @"NEW.""TransactionId""") + @"
END;");
            migrationBuilder.Sql(@"CREATE TRIGGER ""TR_TransactionSplits_Delete_SplitSum"" AFTER DELETE ON ""TransactionSplits""
BEGIN" + string.Format(System.Globalization.CultureInfo.InvariantCulture, recheck, @"OLD.""TransactionId""") + @"
END;");
            migrationBuilder.Sql(@"CREATE TRIGGER ""TR_Transactions_Update_SplitSum"" AFTER UPDATE OF ""Amount"", ""CategoryId"" ON ""Transactions""
BEGIN" + string.Format(System.Globalization.CultureInfo.InvariantCulture, recheck, @"NEW.""Id""") + @"
END;");
            migrationBuilder.Sql(@"CREATE TRIGGER ""TR_Transactions_Delete_SplitSum"" AFTER DELETE ON ""Transactions""
BEGIN
    DELETE FROM ""SplitSumViolations"" WHERE ""TransactionId"" = OLD.""Id"";
END;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS ""TR_Transactions_Delete_SplitSum"";");
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS ""TR_Transactions_Update_SplitSum"";");
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS ""TR_TransactionSplits_Delete_SplitSum"";");
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS ""TR_TransactionSplits_Update_SplitSum"";");
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS ""TR_TransactionSplits_Insert_SplitSum"";");
            migrationBuilder.Sql(@"DROP TABLE IF EXISTS ""SplitSumViolations"";");
            migrationBuilder.Sql(@"DROP TABLE IF EXISTS ""SplitSumGuard"";");

            migrationBuilder.DropTable(
                name: "Alerts");

            migrationBuilder.DropTable(
                name: "Attachments");

            migrationBuilder.DropTable(
                name: "AuditEvents");

            migrationBuilder.DropTable(
                name: "BalanceSnapshots");

            migrationBuilder.DropTable(
                name: "BudgetAssignments");

            migrationBuilder.DropTable(
                name: "Reconciliations");

            migrationBuilder.DropTable(
                name: "Rules");

            migrationBuilder.DropTable(
                name: "Settings");

            migrationBuilder.DropTable(
                name: "Targets");

            migrationBuilder.DropTable(
                name: "TransactionSplits");

            migrationBuilder.DropTable(
                name: "TransactionTags");

            migrationBuilder.DropTable(
                name: "RecurringItems");

            migrationBuilder.DropTable(
                name: "Tags");

            migrationBuilder.DropTable(
                name: "Transactions");

            migrationBuilder.DropTable(
                name: "ScheduledTransactions");

            migrationBuilder.DropTable(
                name: "Payees");

            migrationBuilder.DropTable(
                name: "Categories");

            migrationBuilder.DropTable(
                name: "Accounts");

            migrationBuilder.DropTable(
                name: "CategoryGroups");

            migrationBuilder.DropTable(
                name: "Profiles");

            migrationBuilder.DropTable(
                name: "SyncConnections");
        }
    }
}
