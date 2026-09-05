using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orleans.Persistence.EntityFrameworkCore.PostgreSQL.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialPersistenceSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GrainState",
                columns: table => new
                {
                    KeyHash = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    ServiceId = table.Column<string>(type: "text", nullable: false),
                    GrainType = table.Column<string>(type: "text", nullable: false),
                    StateType = table.Column<string>(type: "text", nullable: false),
                    GrainId = table.Column<string>(type: "text", nullable: false),
                    Data = table.Column<byte[]>(type: "bytea", nullable: true),
                    ETag = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GrainState", x => x.KeyHash);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GrainState");
        }
    }
}
