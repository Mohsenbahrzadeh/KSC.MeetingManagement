using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KSC.MeetingManagement.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMeetingCommittee : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CommitteeId",
                table: "meetings",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_meetings_TenantId_CommitteeId",
                table: "meetings",
                columns: new[] { "TenantId", "CommitteeId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_meetings_TenantId_CommitteeId",
                table: "meetings");

            migrationBuilder.DropColumn(
                name: "CommitteeId",
                table: "meetings");
        }
    }
}
