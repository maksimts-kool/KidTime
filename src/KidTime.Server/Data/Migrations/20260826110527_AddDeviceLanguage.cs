using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KidTime.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDeviceLanguage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Language",
                table: "DeviceRules",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                // Existing PCs keep the wording they were installed with.
                defaultValue: "English");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Language",
                table: "DeviceRules");
        }
    }
}
