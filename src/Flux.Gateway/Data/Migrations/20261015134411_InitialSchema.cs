using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Flux.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "plans",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    display_name = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_plans", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "merchants",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    plan_id = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false, defaultValueSql: "'active'"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_merchants", x => x.id);
                    table.CheckConstraint("ck_merchants_status", "status in ('active', 'suspended')");
                    table.ForeignKey(
                        name: "fk_merchants_plans_plan_id",
                        column: x => x.plan_id,
                        principalTable: "plans",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "plan_route_limits",
                columns: table => new
                {
                    plan_id = table.Column<string>(type: "text", nullable: false),
                    route_group = table.Column<string>(type: "text", nullable: false),
                    refill_per_second = table.Column<decimal>(type: "numeric(10,3)", precision: 10, scale: 3, nullable: false),
                    burst_capacity = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_plan_route_limits", x => new { x.plan_id, x.route_group });
                    table.CheckConstraint("ck_plan_route_limits_burst", "burst_capacity > 0");
                    table.CheckConstraint("ck_plan_route_limits_refill", "refill_per_second > 0");
                    table.CheckConstraint("ck_plan_route_limits_route_group", "route_group in ('payments', 'refunds')");
                    table.ForeignKey(
                        name: "fk_plan_route_limits_plans_plan_id",
                        column: x => x.plan_id,
                        principalTable: "plans",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "api_keys",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    merchant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key_prefix = table.Column<string>(type: "text", nullable: false),
                    key_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_api_keys", x => x.id);
                    table.ForeignKey(
                        name: "fk_api_keys_merchants_merchant_id",
                        column: x => x.merchant_id,
                        principalTable: "merchants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "plans",
                columns: new[] { "id", "display_name" },
                values: new object[,]
                {
                    { "free", "Free" },
                    { "pro", "Pro" }
                });

            migrationBuilder.InsertData(
                table: "plan_route_limits",
                columns: new[] { "plan_id", "route_group", "burst_capacity", "refill_per_second" },
                values: new object[,]
                {
                    { "free", "payments", 10, 10m },
                    { "free", "refunds", 2, 2m },
                    { "pro", "payments", 100, 100m },
                    { "pro", "refunds", 20, 20m }
                });

            migrationBuilder.CreateIndex(
                name: "ix_api_keys_key_hash",
                table: "api_keys",
                column: "key_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_api_keys_merchant",
                table: "api_keys",
                column: "merchant_id");

            migrationBuilder.CreateIndex(
                name: "ix_merchants_plan_id",
                table: "merchants",
                column: "plan_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "api_keys");

            migrationBuilder.DropTable(
                name: "plan_route_limits");

            migrationBuilder.DropTable(
                name: "merchants");

            migrationBuilder.DropTable(
                name: "plans");
        }
    }
}
