using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Robust.Cdn.Data.Migrations.Manifest
{
    /// <inheritdoc />
    public partial class InitialManifest : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Fork",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "text", nullable: false),
                    ServerManifestCache = table.Column<byte[]>(type: "bytea", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Fork", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ForkVersion",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ForkId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    PublishedTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ClientFileName = table.Column<string>(type: "text", nullable: false),
                    Sha256 = table.Column<byte[]>(type: "bytea", nullable: false),
                    EngineVersion = table.Column<string>(type: "text", nullable: true),
                    Available = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ForkVersion", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ForkVersion_Fork_ForkId",
                        column: x => x.ForkId,
                        principalTable: "Fork",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PublishInProgress",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Version = table.Column<string>(type: "text", nullable: false),
                    ForkId = table.Column<int>(type: "integer", nullable: false),
                    StartTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EngineVersion = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublishInProgress", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PublishInProgress_Fork_ForkId",
                        column: x => x.ForkId,
                        principalTable: "Fork",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ForkVersionServerBuild",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ForkVersionId = table.Column<int>(type: "integer", nullable: false),
                    Platform = table.Column<string>(type: "text", nullable: false),
                    FileName = table.Column<string>(type: "text", nullable: false),
                    Sha256 = table.Column<byte[]>(type: "bytea", nullable: false),
                    FileSize = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ForkVersionServerBuild", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ForkVersionServerBuild_ForkVersion_ForkVersionId",
                        column: x => x.ForkVersionId,
                        principalTable: "ForkVersion",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Fork_Name",
                table: "Fork",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ForkVersion_ForkId_Name",
                table: "ForkVersion",
                columns: new[] { "ForkId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ForkVersionServerBuild_ForkVersionId_FileName",
                table: "ForkVersionServerBuild",
                columns: new[] { "ForkVersionId", "FileName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ForkVersionServerBuild_ForkVersionId_Platform",
                table: "ForkVersionServerBuild",
                columns: new[] { "ForkVersionId", "Platform" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PublishInProgress_ForkId_Version",
                table: "PublishInProgress",
                columns: new[] { "ForkId", "Version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ForkVersionServerBuild");

            migrationBuilder.DropTable(
                name: "PublishInProgress");

            migrationBuilder.DropTable(
                name: "ForkVersion");

            migrationBuilder.DropTable(
                name: "Fork");
        }
    }
}
