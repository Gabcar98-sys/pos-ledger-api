using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PosLedger.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SalesAndIdempotency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateSequence(
                name: "sale_number_seq");

            migrationBuilder.CreateTable(
                name: "idempotency_records",
                columns: table => new
                {
                    key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    endpoint = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    request_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    response_status_code = table.Column<int>(type: "integer", nullable: false),
                    response_body = table.Column<string>(type: "jsonb", nullable: true),
                    sale_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_idempotency_records", x => x.key);
                });

            migrationBuilder.CreateTable(
                name: "sales",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<long>(type: "bigint", nullable: false, defaultValueSql: "nextval('sale_number_seq')"),
                    cashier_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    total = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sales", x => x.id);
                    table.CheckConstraint("ck_sales_total_non_negative", "total >= 0");
                });

            migrationBuilder.CreateTable(
                name: "sale_lines",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    sale_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sku = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    product_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    unit_price = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    line_total = table.Column<decimal>(type: "numeric(18,2)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sale_lines", x => x.id);
                    table.CheckConstraint("ck_sale_lines_quantity_positive", "quantity > 0");
                    table.CheckConstraint("ck_sale_lines_total_matches", "line_total = unit_price * quantity");
                    table.ForeignKey(
                        name: "fk_sale_lines_products_product_id",
                        column: x => x.product_id,
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sale_lines_sales_sale_id",
                        column: x => x.sale_id,
                        principalTable: "sales",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_idempotency_records_created_at",
                table: "idempotency_records",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_sale_lines_product_id",
                table: "sale_lines",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "ix_sale_lines_sale_id",
                table: "sale_lines",
                column: "sale_id");

            migrationBuilder.CreateIndex(
                name: "ix_sales_number",
                table: "sales",
                column: "number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sales_occurred_at",
                table: "sales",
                column: "occurred_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "idempotency_records");

            migrationBuilder.DropTable(
                name: "sale_lines");

            migrationBuilder.DropTable(
                name: "sales");

            migrationBuilder.DropSequence(
                name: "sale_number_seq");
        }
    }
}
