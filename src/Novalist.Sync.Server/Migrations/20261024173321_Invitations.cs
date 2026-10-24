using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Novalist.Sync.Server.Migrations
{
    /// <inheritdoc />
    public partial class Invitations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "invitations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    redeemed_by = table.Column<Guid>(type: "uuid", nullable: true),
                    activated_device_id = table.Column<Guid>(type: "uuid", nullable: true),
                    certificate_hash = table.Column<byte[]>(type: "bytea", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_invitations", x => x.id);
                    table.ForeignKey(
                        name: "fk_invitations_devices_activated_device_id",
                        column: x => x.activated_device_id,
                        principalTable: "devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_invitations_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_invitations_users_redeemed_by",
                        column: x => x.redeemed_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_invitations_activated_device_id",
                table: "invitations",
                column: "activated_device_id");

            migrationBuilder.CreateIndex(
                name: "ix_invitations_project_id",
                table: "invitations",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ix_invitations_redeemed_by",
                table: "invitations",
                column: "redeemed_by");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "invitations");
        }
    }
}
