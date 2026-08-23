using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KidTime.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class ScopeRulesToWindowsAccount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "WindowsUsersJson",
                table: "Devices",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'[]'::jsonb");

            migrationBuilder.AddColumn<string>(
                name: "ControlledUserName",
                table: "DeviceRules",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ControlledUserSid",
                table: "DeviceRules",
                type: "character varying(184)",
                maxLength: 184,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "WindowsUsersJson",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "ControlledUserName",
                table: "DeviceRules");

            migrationBuilder.DropColumn(
                name: "ControlledUserSid",
                table: "DeviceRules");
        }
    }
}
