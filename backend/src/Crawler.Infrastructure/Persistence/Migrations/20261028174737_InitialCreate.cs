using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Crawler.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "jobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    InputUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    StartingDomain = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    MaxDepth = table.Column<int>(type: "integer", nullable: false),
                    MaxPages = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    FailureReason = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PagesCrawled = table.Column<int>(type: "integer", nullable: false),
                    PagesDiscovered = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_jobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "pages",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    Url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    Depth = table.Column<int>(type: "integer", nullable: false),
                    ParentPageId = table.Column<long>(type: "bigint", nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    HttpStatusCode = table.Column<int>(type: "integer", nullable: true),
                    Title = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    Error = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    OutgoingLinkCount = table.Column<int>(type: "integer", nullable: false),
                    InternalLinkCount = table.Column<int>(type: "integer", nullable: false),
                    DomainLinkRatio = table.Column<double>(type: "double precision", nullable: false),
                    CrawledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_pages_jobs_JobId",
                        column: x => x.JobId,
                        principalTable: "jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_pages_pages_ParentPageId",
                        column: x => x.ParentPageId,
                        principalTable: "pages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "edges",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParentPageId = table.Column<long>(type: "bigint", nullable: false),
                    ChildPageId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_edges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_edges_jobs_JobId",
                        column: x => x.JobId,
                        principalTable: "jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_edges_pages_ChildPageId",
                        column: x => x.ChildPageId,
                        principalTable: "pages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_edges_pages_ParentPageId",
                        column: x => x.ParentPageId,
                        principalTable: "pages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_edges_ChildPageId",
                table: "edges",
                column: "ChildPageId");

            migrationBuilder.CreateIndex(
                name: "IX_edges_JobId_ParentPageId_ChildPageId",
                table: "edges",
                columns: new[] { "JobId", "ParentPageId", "ChildPageId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_edges_ParentPageId",
                table: "edges",
                column: "ParentPageId");

            migrationBuilder.CreateIndex(
                name: "IX_jobs_CreatedAt",
                table: "jobs",
                column: "CreatedAt",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "IX_jobs_Status",
                table: "jobs",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_pages_JobId_Depth",
                table: "pages",
                columns: new[] { "JobId", "Depth" });

            migrationBuilder.CreateIndex(
                name: "IX_pages_JobId_Url",
                table: "pages",
                columns: new[] { "JobId", "Url" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_pages_ParentPageId",
                table: "pages",
                column: "ParentPageId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "edges");

            migrationBuilder.DropTable(
                name: "pages");

            migrationBuilder.DropTable(
                name: "jobs");
        }
    }
}
