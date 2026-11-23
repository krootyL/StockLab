using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StockLab.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RenameScenariosToStrategies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_orders_scenarios_ScenarioId",
                table: "orders");

            migrationBuilder.DropForeignKey(
                name: "FK_scenarios_users_Uid",
                table: "scenarios");

            migrationBuilder.DropForeignKey(
                name: "FK_stocks_scenarios_ScenarioId",
                table: "stocks");

            migrationBuilder.DropForeignKey(
                name: "FK_trades_scenarios_ScenarioId",
                table: "trades");

            migrationBuilder.DropForeignKey(
                name: "FK_wallets_scenarios_ScenarioId",
                table: "wallets");

            migrationBuilder.DropPrimaryKey(
                name: "PK_scenarios",
                table: "scenarios");

            migrationBuilder.RenameTable(
                name: "scenarios",
                newName: "strategies");

            migrationBuilder.RenameIndex(
                name: "IX_scenarios_Uid",
                table: "strategies",
                newName: "IX_strategies_Uid");

            migrationBuilder.AddPrimaryKey(
                name: "PK_strategies",
                table: "strategies",
                column: "Id");

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
                name: "FK_strategies_users_Uid",
                table: "strategies",
                column: "Uid",
                principalTable: "users",
                principalColumn: "Uid",
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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_orders_strategies_ScenarioId",
                table: "orders");

            migrationBuilder.DropForeignKey(
                name: "FK_stocks_strategies_ScenarioId",
                table: "stocks");

            migrationBuilder.DropForeignKey(
                name: "FK_strategies_users_Uid",
                table: "strategies");

            migrationBuilder.DropForeignKey(
                name: "FK_trades_strategies_ScenarioId",
                table: "trades");

            migrationBuilder.DropForeignKey(
                name: "FK_wallets_strategies_ScenarioId",
                table: "wallets");

            migrationBuilder.DropPrimaryKey(
                name: "PK_strategies",
                table: "strategies");

            migrationBuilder.RenameTable(
                name: "strategies",
                newName: "scenarios");

            migrationBuilder.RenameIndex(
                name: "IX_strategies_Uid",
                table: "scenarios",
                newName: "IX_scenarios_Uid");

            migrationBuilder.AddPrimaryKey(
                name: "PK_scenarios",
                table: "scenarios",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_orders_scenarios_ScenarioId",
                table: "orders",
                column: "ScenarioId",
                principalTable: "scenarios",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_scenarios_users_Uid",
                table: "scenarios",
                column: "Uid",
                principalTable: "users",
                principalColumn: "Uid",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_stocks_scenarios_ScenarioId",
                table: "stocks",
                column: "ScenarioId",
                principalTable: "scenarios",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_trades_scenarios_ScenarioId",
                table: "trades",
                column: "ScenarioId",
                principalTable: "scenarios",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_wallets_scenarios_ScenarioId",
                table: "wallets",
                column: "ScenarioId",
                principalTable: "scenarios",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
