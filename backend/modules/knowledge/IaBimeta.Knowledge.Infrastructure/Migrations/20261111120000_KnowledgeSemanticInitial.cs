using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IaBimeta.Knowledge.Infrastructure.Migrations
{
    /// <summary>
    /// O índice semântico (ADR 0034). Escrita à mão: o contexto não mapeia entidades porque a coluna usa o tipo <c>vector</c> do pgvector. Exige a extensão <c>vector</c> no PostgreSQL de destino
    /// (<c>CREATE EXTENSION</c> precisa de privilégio; na imagem <c>pgvector/pgvector:pg17</c> o usuário do Compose o tem). Só é aplicada quando <c>Knowledge:Semantic:Enabled</c> é verdadeiro.
    /// Reversão: <c>Down</c> remove a tabela e o esquema, sem remover a extensão (outros usos).
    /// </summary>
    [DbContext(typeof(KnowledgeDbContext))]
    [Migration("20261111120000_KnowledgeSemanticInitial")]
    public sealed class KnowledgeSemanticInitial : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS vector;");
            migrationBuilder.Sql("CREATE SCHEMA IF NOT EXISTS knowledge;");
            migrationBuilder.Sql(
                """
                CREATE TABLE knowledge.semantic_texts (
                    tenant_id uuid NOT NULL,
                    entry_ordinal integer NOT NULL,
                    kind smallint NOT NULL,
                    alias_ordinal integer NOT NULL,
                    definition_number integer NOT NULL,
                    locale character varying(35) NOT NULL,
                    auto_reply boolean NOT NULL,
                    text character varying(1000) NOT NULL,
                    text_hash character varying(64) NOT NULL,
                    model_id character varying(100) NOT NULL,
                    dimension integer NOT NULL,
                    status smallint NOT NULL,
                    embedding vector(1024) NULL,
                    attempts integer NOT NULL DEFAULT 0,
                    last_error character varying(60) NULL,
                    next_attempt_at timestamp with time zone NULL,
                    claimed_until timestamp with time zone NULL,
                    created_at timestamp with time zone NOT NULL,
                    updated_at timestamp with time zone NOT NULL,
                    CONSTRAINT pk_semantic_texts PRIMARY KEY (tenant_id, entry_ordinal, kind, alias_ordinal),
                    CONSTRAINT ck_semantic_texts_kind CHECK (kind IN (1, 2)),
                    CONSTRAINT ck_semantic_texts_status CHECK (status IN (1, 2, 3)),
                    CONSTRAINT ck_semantic_texts_embedding CHECK ((status = 2) = (embedding IS NOT NULL))
                );
                """);
            migrationBuilder.Sql("CREATE INDEX ix_semantic_texts_pending ON knowledge.semantic_texts (updated_at) WHERE status = 1;");
            migrationBuilder.Sql("CREATE INDEX ix_semantic_texts_model ON knowledge.semantic_texts (model_id, dimension);");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TABLE IF EXISTS knowledge.semantic_texts;");
        }
    }
}
