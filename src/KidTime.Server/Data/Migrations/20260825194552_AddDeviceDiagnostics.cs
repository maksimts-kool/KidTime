using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KidTime.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDeviceDiagnostics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DeviceDiagnosticEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    LastReportId = table.Column<Guid>(type: "uuid", nullable: false),
                    Fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Component = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Severity = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Message = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    ExceptionType = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Detail = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    AgentVersion = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    OccurrenceCount = table.Column<int>(type: "integer", nullable: false),
                    FirstOccurredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastOccurredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReceivedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ResolvedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceDiagnosticEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeviceDiagnosticEvents_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DeviceDiagnosticEvents_DeviceId_Fingerprint",
                table: "DeviceDiagnosticEvents",
                columns: new[] { "DeviceId", "Fingerprint" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeviceDiagnosticEvents_DeviceId_LastOccurredAtUtc",
                table: "DeviceDiagnosticEvents",
                columns: new[] { "DeviceId", "LastOccurredAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeviceDiagnosticEvents");
        }
    }
}
