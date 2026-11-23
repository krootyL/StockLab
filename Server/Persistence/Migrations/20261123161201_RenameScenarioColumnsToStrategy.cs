using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StockLab.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RenameScenarioColumnsToStrategy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_orders_strategies_ScenarioId",
                table: "orders");

            migrationBuilder.DropForeignKey(
                name: "FK_stocks_strategies_ScenarioId",
                table: "stocks");

            migrationBuilder.DropForeignKey(
                name: "FK_trades_strategies_ScenarioId",
                table: "trades");

            migrationBuilder.DropForeignKey(
                name: "FK_wallets_strategies_ScenarioId",
                table: "wallets");

            migrationBuilder.RenameColumn(
                name: "ScenarioId",
                table: "wallets",
                newName: "StrategyId");

            migrationBuilder.RenameIndex(
                name: "IX_wallets_ScenarioId_Currency",
                table: "wallets",
                newName: "IX_wallets_StrategyId_Currency");

            migrationBuilder.RenameColumn(
                name: "ScenarioId",
                table: "trades",
                newName: "StrategyId");

            migrationBuilder.RenameIndex(
                name: "IX_trades_ScenarioId_ExecutedAtUtc",
                table: "trades",
                newName: "IX_trades_StrategyId_ExecutedAtUtc");

            migrationBuilder.RenameColumn(
                name: "ScenarioId",
                table: "stocks",
                newName: "StrategyId");

            migrationBuilder.RenameIndex(
                name: "IX_stocks_ScenarioId_StockCode",
                table: "stocks",
                newName: "IX_stocks_StrategyId_StockCode");

            migrationBuilder.RenameColumn(
                name: "ScenarioId",
                table: "orders",
                newName: "StrategyId");

            migrationBuilder.RenameIndex(
                name: "IX_orders_ScenarioId_OrderedAtUtc",
                table: "orders",
                newName: "IX_orders_StrategyId_OrderedAtUtc");

            migrationBuilder.AddForeignKey(
                name: "FK_orders_strategies_StrategyId",
                table: "orders",
                column: "StrategyId",
                principalTable: "strategies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_stocks_strategies_StrategyId",
                table: "stocks",
                column: "StrategyId",
                principalTable: "strategies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_trades_strategies_StrategyId",
                table: "trades",
                column: "StrategyId",
                principalTable: "strategies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_wallets_strategies_StrategyId",
                table: "wallets",
                column: "StrategyId",
                principalTable: "strategies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_orders_strategies_StrategyId",
                table: "orders");

            migrationBuilder.DropForeignKey(
                name: "FK_stocks_strategies_StrategyId",
                table: "stocks");

            migrationBuilder.DropForeignKey(
                name: "FK_trades_strategies_StrategyId",
                table: "trades");

            migrationBuilder.DropForeignKey(
                name: "FK_wallets_strategies_StrategyId",
                table: "wallets");

            migrationBuilder.RenameColumn(
                name: "StrategyId",
                table: "wallets",
                newName: "ScenarioId");

            migrationBuilder.RenameIndex(
                name: "IX_wallets_StrategyId_Currency",
                table: "wallets",
                newName: "IX_wallets_ScenarioId_Currency");

            migrationBuilder.RenameColumn(
                name: "StrategyId",
                table: "trades",
                newName: "ScenarioId");

            migrationBuilder.RenameIndex(
                name: "IX_trades_StrategyId_ExecutedAtUtc",
                table: "trades",
                newName: "IX_trades_ScenarioId_ExecutedAtUtc");

            migrationBuilder.RenameColumn(
                name: "StrategyId",
                table: "stocks",
                newName: "ScenarioId");

            migrationBuilder.RenameIndex(
                name: "IX_stocks_StrategyId_StockCode",
                table: "stocks",
                newName: "IX_stocks_ScenarioId_StockCode");

            migrationBuilder.RenameColumn(
                name: "StrategyId",
                table: "orders",
                newName: "ScenarioId");

            migrationBuilder.RenameIndex(
                name: "IX_orders_StrategyId_OrderedAtUtc",
                table: "orders",
                newName: "IX_orders_ScenarioId_OrderedAtUtc");

            migrationBuilder.AddForeignKey(
                name: "FK_orders_strategies_ScenarioId",
                table: "orders",
                column: "ScenarioId",
                principalTable: "strategies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_stocks_strategies_ScenarioId",
                table: "stocks",
                column: "ScenarioId",
                principalTable: "strategies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_trades_strategies_ScenarioId",
                table: "trades",
                column: "ScenarioId",
                principalTable: "strategies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_wallets_strategies_ScenarioId",
                table: "wallets",
                column: "ScenarioId",
                principalTable: "strategies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
