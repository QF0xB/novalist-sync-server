using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Novalist.Sync.Server.Migrations
{
    /// <inheritdoc />
    public partial class ProjectEpochs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "project_device_authorizations",
                columns: table => new
                {
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    epoch = table.Column<long>(type: "bigint", nullable: false),
                    certificate = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_project_device_authorizations", x => new { x.project_id, x.device_id });
                    table.ForeignKey(
                        name: "fk_project_device_authorizations_devices_device_id",
                        column: x => x.device_id,
                        principalTable: "devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_project_device_authorizations_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "project_epoch_key_grants",
                columns: table => new
                {
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    epoch = table.Column<long>(type: "bigint", nullable: false),
                    first_server_seq = table.Column<long>(type: "bigint", nullable: false),
                    grantor_device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payload = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_project_epoch_key_grants", x => new { x.project_id, x.device_id, x.epoch });
                    table.ForeignKey(
                        name: "fk_project_epoch_key_grants_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "project_epoch_stages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    previous_epoch = table.Column<long>(type: "bigint", nullable: false),
                    previous_server_seq = table.Column<long>(type: "bigint", nullable: false),
                    count = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_project_epoch_stages", x => x.id);
                    table.ForeignKey(
                        name: "fk_project_epoch_stages_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "project_epoch_device_grants",
                columns: table => new
                {
                    rotation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    certificate = table.Column<byte[]>(type: "bytea", nullable: false),
                    payload = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_project_epoch_device_grants", x => new { x.rotation_id, x.device_id });
                    table.ForeignKey(
                        name: "fk_project_epoch_device_grants_project_epoch_stages_rotation_id",
                        column: x => x.rotation_id,
                        principalTable: "project_epoch_stages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "project_epoch_envelopes",
                columns: table => new
                {
                    rotation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    index = table.Column<long>(type: "bigint", nullable: false),
                    sender_seq = table.Column<long>(type: "bigint", nullable: false),
                    payload = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_project_epoch_envelopes", x => new { x.rotation_id, x.index });
                    table.ForeignKey(
                        name: "fk_project_epoch_envelopes_project_epoch_stages_rotation_id",
                        column: x => x.rotation_id,
                        principalTable: "project_epoch_stages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_project_device_authorizations_device_id",
                table: "project_device_authorizations",
                column: "device_id");

            migrationBuilder.CreateIndex(
                name: "ix_project_epoch_envelopes_rotation_id_sender_seq",
                table: "project_epoch_envelopes",
                columns: new[] { "rotation_id", "sender_seq" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_project_epoch_stages_project_id",
                table: "project_epoch_stages",
                column: "project_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "project_device_authorizations");

            migrationBuilder.DropTable(
                name: "project_epoch_device_grants");

            migrationBuilder.DropTable(
                name: "project_epoch_envelopes");

            migrationBuilder.DropTable(
                name: "project_epoch_key_grants");

            migrationBuilder.DropTable(
                name: "project_epoch_stages");
        }
    }
}
