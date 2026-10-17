using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LocalMerch.Core.Migrations
{
    /// <inheritdoc />
    public partial class PaymentTerminal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PaymentTerminals",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Protocol = table.Column<int>(type: "INTEGER", nullable: false),
                    Host = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    Port = table.Column<int>(type: "INTEGER", nullable: true),
                    PosNumber = table.Column<string>(type: "TEXT", nullable: false),
                    TimeoutSeconds = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentTerminals", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PaymentTerminals");
        }
    }
}
