using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LocalMerch.Core.Migrations
{
    /// <inheritdoc />
    public partial class ExitPassword : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExitPassword",
                table: "Settings",
                type: "TEXT",
                maxLength: 20,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExitPassword",
                table: "Settings");
        }
    }
}
