using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KSC.MeetingManagement.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMinutes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "minutes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    MeetingId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CurrentVersionNumber = table.Column<int>(type: "integer", nullable: false),
                    ApprovedVersionNumber = table.Column<int>(type: "integer", nullable: true),
                    LockVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_minutes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "minutes_transitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MinutesId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    From = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    To = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    VersionNumber = table.Column<int>(type: "integer", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Comment = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_minutes_transitions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_minutes_transitions_minutes_MinutesId",
                        column: x => x.MinutesId,
                        principalTable: "minutes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "minutes_versions",
                columns: table => new
                {
                    MinutesId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Content = table.Column<string>(type: "text", nullable: false),
                    AuthorUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_minutes_versions", x => new { x.MinutesId, x.Number });
                    table.ForeignKey(
                        name: "FK_minutes_versions_minutes_MinutesId",
                        column: x => x.MinutesId,
                        principalTable: "minutes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_minutes_TenantId_MeetingId",
                table: "minutes",
                columns: new[] { "TenantId", "MeetingId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_minutes_transitions_MinutesId",
                table: "minutes_transitions",
                column: "MinutesId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "minutes_transitions");

            migrationBuilder.DropTable(
                name: "minutes_versions");

            migrationBuilder.DropTable(
                name: "minutes");
        }
    }
}
