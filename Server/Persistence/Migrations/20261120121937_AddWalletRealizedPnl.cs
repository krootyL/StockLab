using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StockLab.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWalletRealizedPnl : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "RealizedPnl",
                table: "wallets",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: false,
                defaultValue: 0m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RealizedPnl",
                table: "wallets");
        }
    }
}
