using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoveBg.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AsyncImageJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "image_jobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    RequestId = table.Column<Guid>(type: "char(36)", nullable: false),
                    AccountId = table.Column<Guid>(type: "char(36)", nullable: false),
                    ApiKeyId = table.Column<Guid>(type: "char(36)", nullable: false),
                    Status = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    Filename = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false),
                    ContentType = table.Column<string>(type: "varchar(40)", maxLength: 40, nullable: false),
                    InputData = table.Column<byte[]>(type: "longblob", nullable: false),
                    OutputData = table.Column<byte[]>(type: "longblob", nullable: true),
                    InputBytes = table.Column<long>(type: "bigint", nullable: false),
                    OutputBytes = table.Column<long>(type: "bigint", nullable: false),
                    Width = table.Column<int>(type: "int", nullable: false),
                    Height = table.Column<int>(type: "int", nullable: false),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    MaxAttempts = table.Column<int>(type: "int", nullable: false),
                    PriceKopecks = table.Column<long>(type: "bigint", nullable: false),
                    BalanceAfterKopecks = table.Column<long>(type: "bigint", nullable: true),
                    QueueDurationMs = table.Column<long>(type: "bigint", nullable: false),
                    ProcessingDurationMs = table.Column<long>(type: "bigint", nullable: false),
                    CpuTimeMs = table.Column<long>(type: "bigint", nullable: false),
                    PeakRamBytes = table.Column<long>(type: "bigint", nullable: false),
                    QualityScore = table.Column<double>(type: "double", nullable: true),
                    ErrorCode = table.Column<string>(type: "varchar(80)", maxLength: 80, nullable: true),
                    ErrorMessage = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_image_jobs", x => x.Id);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_image_jobs_AccountId_CreatedAt",
                table: "image_jobs",
                columns: new[] { "AccountId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_image_jobs_RequestId",
                table: "image_jobs",
                column: "RequestId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_image_jobs_Status_CreatedAt",
                table: "image_jobs",
                columns: new[] { "Status", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "image_jobs");
        }
    }
}
