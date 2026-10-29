using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Crawler.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HistoryIndexWithIdTieBreaker : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_jobs_CreatedAt",
                table: "jobs");

            migrationBuilder.CreateIndex(
                name: "IX_jobs_CreatedAt_Id",
                table: "jobs",
                columns: new[] { "CreatedAt", "Id" },
                descending: new bool[0]);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_jobs_CreatedAt_Id",
                table: "jobs");

            migrationBuilder.CreateIndex(
                name: "IX_jobs_CreatedAt",
                table: "jobs",
                column: "CreatedAt",
                descending: new bool[0]);
        }
    }
}
