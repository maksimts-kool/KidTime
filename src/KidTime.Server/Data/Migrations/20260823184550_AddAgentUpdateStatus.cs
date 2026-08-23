using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KidTime.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentUpdateStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AgentUpdateCheckedAtUtc",
                table: "Devices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AgentUpdateError",
                table: "Devices",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AgentUpdateStatus",
                table: "Devices",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AgentVersion",
                table: "Devices",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AgentUpdateCheckedAtUtc",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "AgentUpdateError",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "AgentUpdateStatus",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "AgentVersion",
                table: "Devices");
        }
    }
}
