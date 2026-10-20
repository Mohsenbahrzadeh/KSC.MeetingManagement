using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KSC.MeetingManagement.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddActionItems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "action_items",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    MeetingId = table.Column<Guid>(type: "uuid", nullable: false),
                    ResolutionId = table.Column<Guid>(type: "uuid", nullable: true),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    DueDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    LockVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_action_items", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_action_items_TenantId_MeetingId",
                table: "action_items",
                columns: new[] { "TenantId", "MeetingId" });

            migrationBuilder.CreateIndex(
                name: "IX_action_items_TenantId_OwnerUserId_Status",
                table: "action_items",
                columns: new[] { "TenantId", "OwnerUserId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_action_items_TenantId_Status_DueDate",
                table: "action_items",
                columns: new[] { "TenantId", "Status", "DueDate" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "action_items");
        }
    }
}
