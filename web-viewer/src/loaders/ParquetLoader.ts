import * as duckdb from "@duckdb/duckdb-wasm";
import { ElementMetadata } from "../core/CacheSystem";

/**
 * Parquet Loader using DuckDB-WASM
 * Enables browser-side SQL queries on columnar data
 */
export class DTParquetLoader {
    private db: duckdb.AsyncDuckDB | null = null;
    private conn: duckdb.AsyncDuckDBConnection | null = null;
    private parquetUrl: string = "";

    async initialize(parquetUrl: string): Promise<void> {
        console.log("Initializing DuckDB-WASM...");
        this.parquetUrl = parquetUrl;

        try {
            // Initialize DuckDB-WASM
            const JSDELIVR_BUNDLES = duckdb.getJsDelivrBundles();
            const bundle = await duckdb.selectBundle(JSDELIVR_BUNDLES);

            const worker_url = URL.createObjectURL(
                new Blob([`importScripts("${bundle.mainWorker}");`], {
                    type: "text/javascript",
                })
            );

            const worker = new Worker(worker_url);
            const logger = new duckdb.ConsoleLogger();
            this.db = new duckdb.AsyncDuckDB(logger, worker);
            await this.db.instantiate(bundle.mainModule);

            this.conn = await this.db.connect();

            console.log("✓ DuckDB-WASM initialized");
            console.log("Parquet URL:", parquetUrl);
        } catch (error) {
            console.error("Failed to initialize DuckDB-WASM:", error);
            throw error;
        }
    }

    async queryByGuid(guid: string): Promise<ElementMetadata | null> {
        if (!this.conn) {
            console.warn("DuckDB connection not initialized");
            return null;
        }

        try {
            const startTime = performance.now();

            // Query parquet file directly via HTTP Range requests
            const result = await this.conn.query(`
        SELECT *
        FROM read_parquet('${this.parquetUrl}')
        WHERE guid = '${guid}'
        LIMIT 1
      `);

            const latency = performance.now() - startTime;
            console.log(`L3 query latency: ${latency.toFixed(1)}ms`);

            const rows = result.toArray();
            if (rows.length === 0) return null;

            const row = rows[0];

            // Parse JSON parameter fields
            const parseParams = (jsonStr: string) => {
                try {
                    return JSON.parse(jsonStr || "{}");
                } catch {
                    return {};
                }
            };

            return {
                guid: row.guid,
                elementId: row.element_id,
                category: row.category,
                familyName: row.family_name,
                typeName: row.type_name,
                levelName: row.level_name,
                phaseName: row.phase_name,
                volume: row.volume,
                area: row.area,
                boundingBox: [
                    parseFloat(row.bbox_min_x),
                    parseFloat(row.bbox_min_y),
                    parseFloat(row.bbox_min_z),
                    parseFloat(row.bbox_max_x),
                    parseFloat(row.bbox_max_y),
                    parseFloat(row.bbox_max_z),
                ],
                instanceParameters: parseParams(row.instance_parameters),
                typeParameters: parseParams(row.type_parameters),
                builtinParameters: parseParams(row.builtin_parameters),
            };
        } catch (error) {
            console.error("Parquet query error:", error);
            return null;
        }
    }

    async queryByCategory(category: string): Promise<ElementMetadata[]> {
        if (!this.conn) return [];

        try {
            const result = await this.conn.query(`
        SELECT guid, element_id, category, family_name, type_name, level_name
        FROM read_parquet('${this.parquetUrl}')
        WHERE category = '${category}'
      `);

            return result.toArray().map((row: any) => ({
                guid: row.guid,
                elementId: row.element_id,
                category: row.category,
                familyName: row.family_name,
                typeName: row.type_name,
                levelName: row.level_name,
            }));
        } catch (error) {
            console.error("Category query error:", error);
            return [];
        }
    }

    async getStatistics(): Promise<any> {
        if (!this.conn) return null;

        try {
            const result = await this.conn.query(`
        SELECT
          category,
          COUNT(*) as count,
          COUNT(DISTINCT type_name) as type_count
        FROM read_parquet('${this.parquetUrl}')
        GROUP BY category
        ORDER BY count DESC
      `);

            return result.toArray();
        } catch (error) {
            console.error("Statistics query error:", error);
            return null;
        }
    }

    dispose(): void {
        this.conn?.close();
        this.db?.terminate();
    }
}
