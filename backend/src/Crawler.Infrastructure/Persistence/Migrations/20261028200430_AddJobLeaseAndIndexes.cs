using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Crawler.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddJobLeaseAndIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_pages_pages_ParentPageId",
                table: "pages");

            migrationBuilder.DropIndex(
                name: "IX_pages_JobId_Depth",
                table: "pages");

            migrationBuilder.DropIndex(
                name: "IX_jobs_Status",
                table: "jobs");

            migrationBuilder.AddColumn<DateTime>(
                name: "LeaseExpiresAt",
                table: "jobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LeaseOwner",
                table: "jobs",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_pages_JobId_Depth_Id",
                table: "pages",
                columns: new[] { "JobId", "Depth", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_jobs_Status_CreatedAt",
                table: "jobs",
                columns: new[] { "Status", "CreatedAt" });

            migrationBuilder.AddForeignKey(
                name: "FK_pages_pages_ParentPageId",
                table: "pages",
                column: "ParentPageId",
                principalTable: "pages",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_pages_pages_ParentPageId",
                table: "pages");

            migrationBuilder.DropIndex(
                name: "IX_pages_JobId_Depth_Id",
                table: "pages");

            migrationBuilder.DropIndex(
                name: "IX_jobs_Status_CreatedAt",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "LeaseExpiresAt",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "LeaseOwner",
                table: "jobs");

            migrationBuilder.CreateIndex(
                name: "IX_pages_JobId_Depth",
                table: "pages",
                columns: new[] { "JobId", "Depth" });

            migrationBuilder.CreateIndex(
                name: "IX_jobs_Status",
                table: "jobs",
                column: "Status");

            migrationBuilder.AddForeignKey(
                name: "FK_pages_pages_ParentPageId",
                table: "pages",
                column: "ParentPageId",
                principalTable: "pages",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
