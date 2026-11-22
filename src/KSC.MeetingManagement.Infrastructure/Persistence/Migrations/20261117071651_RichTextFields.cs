using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KSC.MeetingManagement.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RichTextFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(name: "Description", table: "action_items", type: "character varying(260096)", maxLength: 260096, nullable: true, oldClrType: typeof(string), oldType: "character varying(4000)", oldMaxLength: 4000);
            migrationBuilder.AlterColumn<string>(name: "Note", table: "action_items", type: "character varying(132096)", maxLength: 132096, nullable: true, oldClrType: typeof(string), oldType: "character varying(2000)", oldMaxLength: 2000);
            migrationBuilder.AlterColumn<string>(name: "Description", table: "meetings", type: "character varying(260096)", maxLength: 260096, nullable: true, oldClrType: typeof(string), oldType: "character varying(4000)", oldMaxLength: 4000);
            migrationBuilder.AlterColumn<string>(name: "CancellationReason", table: "meetings", type: "character varying(68096)", maxLength: 68096, nullable: true, oldClrType: typeof(string), oldType: "character varying(1000)", oldMaxLength: 1000);
            migrationBuilder.AlterColumn<string>(name: "Comment", table: "minutes_transitions", type: "character varying(132096)", maxLength: 132096, nullable: true, oldClrType: typeof(string), oldType: "character varying(2000)", oldMaxLength: 2000);
            migrationBuilder.AlterColumn<string>(name: "Description", table: "agenda_items", type: "character varying(260096)", maxLength: 260096, nullable: true, oldClrType: typeof(string), oldType: "character varying(4000)", oldMaxLength: 4000);
            migrationBuilder.AlterColumn<string>(name: "Description", table: "committees", type: "character varying(132096)", maxLength: 132096, nullable: true, oldClrType: typeof(string), oldType: "character varying(2000)", oldMaxLength: 2000);
            migrationBuilder.AlterColumn<string>(name: "RevocationReason", table: "resolutions", type: "character varying(132096)", maxLength: 132096, nullable: true, oldClrType: typeof(string), oldType: "character varying(2000)", oldMaxLength: 2000);
            migrationBuilder.AlterColumn<string>(name: "Text", table: "resolutions", type: "character varying(260096)", maxLength: 260096, nullable: false, oldClrType: typeof(string), oldType: "character varying(4000)", oldMaxLength: 4000);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(name: "Description", table: "action_items", type: "character varying(4000)", maxLength: 4000, nullable: true, oldClrType: typeof(string), oldType: "character varying(260096)", oldMaxLength: 260096);
            migrationBuilder.AlterColumn<string>(name: "Note", table: "action_items", type: "character varying(2000)", maxLength: 2000, nullable: true, oldClrType: typeof(string), oldType: "character varying(132096)", oldMaxLength: 132096);
            migrationBuilder.AlterColumn<string>(name: "Description", table: "meetings", type: "character varying(4000)", maxLength: 4000, nullable: true, oldClrType: typeof(string), oldType: "character varying(260096)", oldMaxLength: 260096);
            migrationBuilder.AlterColumn<string>(name: "CancellationReason", table: "meetings", type: "character varying(1000)", maxLength: 1000, nullable: true, oldClrType: typeof(string), oldType: "character varying(68096)", oldMaxLength: 68096);
            migrationBuilder.AlterColumn<string>(name: "Comment", table: "minutes_transitions", type: "character varying(2000)", maxLength: 2000, nullable: true, oldClrType: typeof(string), oldType: "character varying(132096)", oldMaxLength: 132096);
            migrationBuilder.AlterColumn<string>(name: "Description", table: "agenda_items", type: "character varying(4000)", maxLength: 4000, nullable: true, oldClrType: typeof(string), oldType: "character varying(260096)", oldMaxLength: 260096);
            migrationBuilder.AlterColumn<string>(name: "Description", table: "committees", type: "character varying(2000)", maxLength: 2000, nullable: true, oldClrType: typeof(string), oldType: "character varying(132096)", oldMaxLength: 132096);
            migrationBuilder.AlterColumn<string>(name: "RevocationReason", table: "resolutions", type: "character varying(2000)", maxLength: 2000, nullable: true, oldClrType: typeof(string), oldType: "character varying(132096)", oldMaxLength: 132096);
            migrationBuilder.AlterColumn<string>(name: "Text", table: "resolutions", type: "character varying(4000)", maxLength: 4000, nullable: false, oldClrType: typeof(string), oldType: "character varying(260096)", oldMaxLength: 260096);
        }
    }
}
