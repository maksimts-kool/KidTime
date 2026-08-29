using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KidTime.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class TimeExtensions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TimeExtensions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceApplicationId = table.Column<Guid>(type: "uuid", nullable: true),
                    ApplicationIdentityKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    DisplayName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    LocalDate = table.Column<DateOnly>(type: "date", nullable: false),
                    RequestedMinutes = table.Column<int>(type: "integer", nullable: false),
                    GrantedMinutes = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    RequestedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReceivedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DecidedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DecidedByParentUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TimeExtensions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TimeExtensions_DeviceApplications_DeviceApplicationId",
                        column: x => x.DeviceApplicationId,
                        principalTable: "DeviceApplications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_TimeExtensions_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TimeExtensions_DeviceApplicationId",
                table: "TimeExtensions",
                column: "DeviceApplicationId");

            migrationBuilder.CreateIndex(
                name: "IX_TimeExtensions_DeviceId_LocalDate_Status",
                table: "TimeExtensions",
                columns: new[] { "DeviceId", "LocalDate", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TimeExtensions");
        }
    }
}
