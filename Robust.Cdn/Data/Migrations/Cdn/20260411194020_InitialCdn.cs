using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Robust.Cdn.Data.Migrations.Cdn
{
    /// <inheritdoc />
    public partial class InitialCdn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Content",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    Size = table.Column<int>(type: "integer", nullable: false),
                    Compression = table.Column<int>(type: "integer", nullable: false),
                    Data = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Content", x => x.Id);
                    table.CheckConstraint("CK_Content_UncompressedSameSize", "\"Compression\" != 0 OR octet_length(\"Data\") = \"Size\"");
                });

            migrationBuilder.CreateTable(
                name: "Fork",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Fork", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RequestLogBlob",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    Data = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RequestLogBlob", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ContentVersion",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ForkId = table.Column<int>(type: "integer", nullable: false),
                    Version = table.Column<string>(type: "text", nullable: false),
                    TimeAdded = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ManifestHash = table.Column<byte[]>(type: "bytea", nullable: false),
                    ManifestData = table.Column<byte[]>(type: "bytea", nullable: false),
                    CountDistinctBlobs = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContentVersion", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ContentVersion_Fork_ForkId",
                        column: x => x.ForkId,
                        principalTable: "Fork",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ContentManifestEntry",
                columns: table => new
                {
                    VersionId = table.Column<int>(type: "integer", nullable: false),
                    ManifestIdx = table.Column<int>(type: "integer", nullable: false),
                    ContentId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContentManifestEntry", x => new { x.VersionId, x.ManifestIdx });
                    table.ForeignKey(
                        name: "FK_ContentManifestEntry_ContentVersion_VersionId",
                        column: x => x.VersionId,
                        principalTable: "ContentVersion",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ContentManifestEntry_Content_ContentId",
                        column: x => x.ContentId,
                        principalTable: "Content",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RequestLog",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Compression = table.Column<int>(type: "integer", nullable: false),
                    Protocol = table.Column<int>(type: "integer", nullable: false),
                    BytesSent = table.Column<int>(type: "integer", nullable: false),
                    VersionId = table.Column<int>(type: "integer", nullable: false),
                    BlobId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RequestLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RequestLog_ContentVersion_VersionId",
                        column: x => x.VersionId,
                        principalTable: "ContentVersion",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RequestLog_RequestLogBlob_BlobId",
                        column: x => x.BlobId,
                        principalTable: "RequestLogBlob",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Content_Hash",
                table: "Content",
                column: "Hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ContentManifestEntryContentId",
                table: "ContentManifestEntry",
                column: "ContentId");

            migrationBuilder.CreateIndex(
                name: "IX_ContentVersion_ForkId_Version",
                table: "ContentVersion",
                columns: new[] { "ForkId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Fork_Name",
                table: "Fork",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RequestLog_BlobId",
                table: "RequestLog",
                column: "BlobId");

            migrationBuilder.CreateIndex(
                name: "IX_RequestLog_VersionId",
                table: "RequestLog",
                column: "VersionId");

            migrationBuilder.CreateIndex(
                name: "IX_RequestLogBlob_Hash",
                table: "RequestLogBlob",
                column: "Hash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ContentManifestEntry");

            migrationBuilder.DropTable(
                name: "RequestLog");

            migrationBuilder.DropTable(
                name: "Content");

            migrationBuilder.DropTable(
                name: "ContentVersion");

            migrationBuilder.DropTable(
                name: "RequestLogBlob");

            migrationBuilder.DropTable(
                name: "Fork");
        }
    }
}
