using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KSC.MeetingManagement.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRoomEntriesAndMeetingTimes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "EndedAtUtc",
                table: "meetings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "StartedAtUtc",
                table: "meetings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "room_entries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    MeetingId = table.Column<Guid>(type: "uuid", nullable: false),
                    Identity = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    RequestedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DecidedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DecidedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    DecisionVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_room_entries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_room_entries_TenantId_MeetingId_Identity",
                table: "room_entries",
                columns: new[] { "TenantId", "MeetingId", "Identity" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "room_entries");

            migrationBuilder.DropColumn(
                name: "EndedAtUtc",
                table: "meetings");

            migrationBuilder.DropColumn(
                name: "StartedAtUtc",
                table: "meetings");
        }
    }
}
