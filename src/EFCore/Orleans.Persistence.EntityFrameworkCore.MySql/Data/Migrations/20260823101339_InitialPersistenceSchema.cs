using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orleans.Persistence.EntityFrameworkCore.MySql.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialPersistenceSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "GrainState",
                columns: table => new
                {
                    KeyHash = table.Column<byte[]>(type: "binary(32)", maxLength: 32, nullable: false),
                    ServiceId = table.Column<string>(type: "longtext", nullable: false, collation: "utf8mb4_bin")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    GrainType = table.Column<string>(type: "longtext", nullable: false, collation: "utf8mb4_bin")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    StateType = table.Column<string>(type: "longtext", nullable: false, collation: "utf8mb4_bin")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    GrainId = table.Column<string>(type: "longtext", nullable: false, collation: "utf8mb4_bin")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Data = table.Column<byte[]>(type: "longblob", nullable: true),
                    ETag = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GrainState", x => x.KeyHash);
                })
                .Annotation("MySql:CharSet", "utf8mb4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GrainState");
        }
    }
}
