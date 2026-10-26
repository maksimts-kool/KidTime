using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KidTime.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class SpecialPeriodsAndParentGrants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "InitiatedByParent",
                table: "TimeExtensions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "SpecialPeriods",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceApplicationId = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EndDate = table.Column<DateOnly>(type: "date", nullable: false),
                    ScheduleJson = table.Column<string>(type: "jsonb", nullable: false),
                    DailyLimitSeconds = table.Column<int>(type: "integer", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpecialPeriods", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SpecialPeriods_DeviceApplications_DeviceApplicationId",
                        column: x => x.DeviceApplicationId,
                        principalTable: "DeviceApplications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SpecialPeriods_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SpecialPeriods_DeviceApplicationId",
                table: "SpecialPeriods",
                column: "DeviceApplicationId");

            migrationBuilder.CreateIndex(
                name: "IX_SpecialPeriods_DeviceId_EndDate",
                table: "SpecialPeriods",
                columns: new[] { "DeviceId", "EndDate" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SpecialPeriods");

            migrationBuilder.DropColumn(
                name: "InitiatedByParent",
                table: "TimeExtensions");
        }
    }
}
