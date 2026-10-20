using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KSC.MeetingManagement.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddResolutions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "resolutions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    MeetingId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    Text = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    AgendaItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    DecidedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DecidedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    RevocationReason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    LockVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_resolutions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_resolutions_AgendaItemId",
                table: "resolutions",
                column: "AgendaItemId");

            migrationBuilder.CreateIndex(
                name: "IX_resolutions_TenantId_MeetingId_Number",
                table: "resolutions",
                columns: new[] { "TenantId", "MeetingId", "Number" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "resolutions");
        }
    }
}
