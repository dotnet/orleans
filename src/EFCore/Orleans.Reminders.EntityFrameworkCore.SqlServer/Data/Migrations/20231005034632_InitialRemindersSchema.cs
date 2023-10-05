using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orleans.Reminders.EntityFrameworkCore.SqlServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialRemindersSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Reminders",
                columns: table => new
                {
                    ServiceId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    GrainId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    StartAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Period = table.Column<TimeSpan>(type: "time", nullable: false),
                    GrainHash = table.Column<long>(type: "bigint", nullable: false),
                    ETag = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Reminders", x => new { x.ServiceId, x.GrainId, x.Name });
                });

            migrationBuilder.CreateIndex(
                name: "IDX_Reminders_ServiceId_GrainHash",
                table: "Reminders",
                columns: new[] { "ServiceId", "GrainHash" })
                .Annotation("SqlServer:Clustered", false);

            migrationBuilder.CreateIndex(
                name: "IDX_Reminders_ServiceId_GrainId",
                table: "Reminders",
                columns: new[] { "ServiceId", "GrainId" })
                .Annotation("SqlServer:Clustered", false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Reminders");
        }
    }
}
