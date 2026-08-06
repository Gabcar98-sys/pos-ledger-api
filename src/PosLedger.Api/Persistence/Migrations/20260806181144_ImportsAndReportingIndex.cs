using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PosLedger.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ImportsAndReportingIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "import_batches",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    file_name = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: false),
                    uploaded_by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    rows_read = table.Column<int>(type: "integer", nullable: false),
                    rows_accepted = table.Column<int>(type: "integer", nullable: false),
                    rows_rejected = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_import_batches", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "import_errors",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    batch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_number = table.Column<int>(type: "integer", nullable: false),
                    rule = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    raw_line = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_import_errors", x => x.id);
                    table.ForeignKey(
                        name: "fk_import_errors_import_batches_batch_id",
                        column: x => x.batch_id,
                        principalTable: "import_batches",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_stock_movements_occurred_at_reason",
                table: "stock_movements",
                columns: new[] { "occurred_at", "reason" })
                .Annotation("Npgsql:IndexInclude", new[] { "product_id", "delta" });

            migrationBuilder.CreateIndex(
                name: "ix_import_batches_created_at",
                table: "import_batches",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_import_errors_batch_id_rule",
                table: "import_errors",
                columns: new[] { "batch_id", "rule" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "import_errors");

            migrationBuilder.DropTable(
                name: "import_batches");

            migrationBuilder.DropIndex(
                name: "ix_stock_movements_occurred_at_reason",
                table: "stock_movements");
        }
    }
}
