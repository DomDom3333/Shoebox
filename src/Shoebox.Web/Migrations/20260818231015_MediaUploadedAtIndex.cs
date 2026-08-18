using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Shoebox.Web.Migrations
{
    /// <inheritdoc />
    public partial class MediaUploadedAtIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Media_PoolId_UploadedAt",
                table: "Media",
                columns: new[] { "PoolId", "UploadedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Media_PoolId_UploadedAt",
                table: "Media");
        }
    }
}
