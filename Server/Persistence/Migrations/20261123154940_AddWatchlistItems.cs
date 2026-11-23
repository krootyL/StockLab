using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace StockLab.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWatchlistItems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "watchlist_items",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Uid = table.Column<long>(type: "bigint", nullable: false),
                    StockCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_watchlist_items", x => x.Id);
                    table.ForeignKey(
                        name: "FK_watchlist_items_users_Uid",
                        column: x => x.Uid,
                        principalTable: "users",
                        principalColumn: "Uid",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_watchlist_items_Uid_CreatedAtUtc",
                table: "watchlist_items",
                columns: new[] { "Uid", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_watchlist_items_Uid_StockCode",
                table: "watchlist_items",
                columns: new[] { "Uid", "StockCode" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "watchlist_items");
        }
    }
}
