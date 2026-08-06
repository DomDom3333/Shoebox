using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Shoebox.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddPoolWrappedKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "WrappedKey",
                table: "Pools",
                type: "BLOB",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "WrappedKey",
                table: "Pools");
        }
    }
}
