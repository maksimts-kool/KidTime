using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KidTime.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddOpenWindowAtSignIn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "OpenWindowAtSignIn",
                table: "DeviceRules",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OpenWindowAtSignIn",
                table: "DeviceRules");
        }
    }
}
