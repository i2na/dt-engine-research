/**
 * 4-Tier Cache System for Click-to-Data Loop
 * L1: glTF inline metadata (0ms)
 * L2: IndexedDB local cache (<5ms)
 * L3: DuckDB-WASM Parquet query (<100ms)
 * L4: PostgreSQL server API (<300ms)
 */

export interface ElementMetadata {
    guid: string;
    elementId: number;
    category: string;
    familyName?: string;
    typeName?: string;
    levelName?: string;
    phaseName?: string;
    volume?: number;
    area?: number;
    boundingBox?: number[];
    instanceParameters?: Record<string, any>;
    typeParameters?: Record<string, any>;
    builtinParameters?: Record<string, any>;
}

/**
 * L1 Cache: In-memory glTF metadata
 * Instant access (0ms latency)
 */
export class L1InlineCache {
    private cache = new Map<string, Partial<ElementMetadata>>();

    set(guid: string, data: Partial<ElementMetadata>): void {
        this.cache.set(guid, data);
    }

    get(guid: string): Partial<ElementMetadata> | null {
        return this.cache.get(guid) || null;
    }

    clear(): void {
        this.cache.clear();
    }

    size(): number {
        return this.cache.size;
    }
}

/**
 * L2 Cache: IndexedDB browser storage
 * Fast local access (<5ms latency)
 */
export class L2IndexedDBCache {
    private dbName = "dt-engine-cache";
    private storeName = "elements";
    private db: IDBDatabase | null = null;

    async initialize(): Promise<void> {
        return new Promise((resolve, reject) => {
            const request = indexedDB.open(this.dbName, 1);

            request.onerror = () => reject(request.error);
            request.onsuccess = () => {
                this.db = request.result;
                resolve();
            };

            request.onupgradeneeded = (event) => {
                const db = (event.target as IDBOpenDBRequest).result;
                if (!db.objectStoreNames.contains(this.storeName)) {
                    db.createObjectStore(this.storeName, { keyPath: "guid" });
                }
            };
        });
    }

    async get(guid: string): Promise<ElementMetadata | null> {
        if (!this.db) return null;

        return new Promise((resolve, reject) => {
            const transaction = this.db!.transaction([this.storeName], "readonly");
            const store = transaction.objectStore(this.storeName);
            const request = store.get(guid);

            request.onsuccess = () => resolve(request.result || null);
            request.onerror = () => reject(request.error);
        });
    }

    async set(guid: string, data: ElementMetadata): Promise<void> {
        if (!this.db) return;

        return new Promise((resolve, reject) => {
            const transaction = this.db!.transaction([this.storeName], "readwrite");
            const store = transaction.objectStore(this.storeName);
            const request = store.put(data);

            request.onsuccess = () => resolve();
            request.onerror = () => reject(request.error);
        });
    }

    async clear(): Promise<void> {
        if (!this.db) return;

        return new Promise((resolve, reject) => {
            const transaction = this.db!.transaction([this.storeName], "readwrite");
            const store = transaction.objectStore(this.storeName);
            const request = store.clear();

            request.onsuccess = () => resolve();
            request.onerror = () => reject(request.error);
        });
    }
}

/**
 * Unified Cache Manager
 * Orchestrates L1-L4 cache hierarchy
 */
export class CacheManager {
    private l1Cache = new L1InlineCache();
    private l2Cache = new L2IndexedDBCache();
    private parquetLoader: any = null; // Will be set by ParquetLoader

    async initialize(): Promise<void> {
        await this.l2Cache.initialize();
        console.log("✓ Cache system initialized");
    }

    setParquetLoader(loader: any): void {
        this.parquetLoader = loader;
    }

    /**
     * Unified lookup with cascade fallback
     * Returns metadata with source indicator
     */
    async lookup(guid: string): Promise<{
        data: ElementMetadata | null;
        source: "L1" | "L2" | "L3" | "L4";
        latency: number;
    }> {
        const startTime = performance.now();

        // L1: Inline cache (0ms)
        const l1Data = this.l1Cache.get(guid);
        if (l1Data && this.isComplete(l1Data)) {
            return {
                data: l1Data as ElementMetadata,
                source: "L1",
                latency: performance.now() - startTime,
            };
        }

        // L2: IndexedDB (<5ms)
        const l2Data = await this.l2Cache.get(guid);
        if (l2Data) {
            // Update L1 cache
            this.l1Cache.set(guid, l2Data);
            return {
                data: l2Data,
                source: "L2",
                latency: performance.now() - startTime,
            };
        }

        // L3: DuckDB-WASM Parquet (<100ms)
        if (this.parquetLoader) {
            try {
                const l3Data = await this.parquetLoader.queryByGuid(guid);
                if (l3Data) {
                    // Update L2 and L1 caches
                    await this.l2Cache.set(guid, l3Data);
                    this.l1Cache.set(guid, l3Data);
                    return {
                        data: l3Data,
                        source: "L3",
                        latency: performance.now() - startTime,
                    };
                }
            } catch (e) {
                console.error("L3 cache error:", e);
            }
        }

        // L4: Server API (future implementation)
        return {
            data: null,
            source: "L4",
            latency: performance.now() - startTime,
        };
    }

    /**
     * Preload inline metadata from glTF
     */
    preloadInline(guid: string, data: Partial<ElementMetadata>): void {
        this.l1Cache.set(guid, data);
    }

    /**
     * Check if metadata is complete
     */
    private isComplete(data: Partial<ElementMetadata>): boolean {
        return !!(data.guid && data.category && data.instanceParameters);
    }

    getStats() {
        return {
            l1Size: this.l1Cache.size(),
        };
    }
}
