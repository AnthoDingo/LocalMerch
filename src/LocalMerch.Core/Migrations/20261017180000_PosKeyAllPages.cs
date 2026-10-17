using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LocalMerch.Core.Migrations
{
    /// <inheritdoc />
    public partial class PosKeyAllPages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AllPages",
                table: "PosKeys",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AllPages",
                table: "PosKeys");
        }
    }
}
