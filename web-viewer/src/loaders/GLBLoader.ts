import * as THREE from "three";
import { GLTFLoader } from "three/examples/jsm/loaders/GLTFLoader.js";
import { CacheManager } from "../core/CacheSystem";

/**
 * GLB Loader with GUID extraction from EXT_structural_metadata
 */
export class DTGLBLoader {
    private loader = new GLTFLoader();
    private guidToMesh = new Map<string, THREE.Object3D>();
    private meshToGuid = new Map<THREE.Object3D, string>();
    private cacheManager: CacheManager;

    constructor(cacheManager: CacheManager) {
        this.cacheManager = cacheManager;
    }

    async load(url: string): Promise<THREE.Group> {
        console.log("Loading GLB:", url);

        return new Promise((resolve, reject) => {
            this.loader.load(
                url,
                (gltf) => {
                    console.log("✓ GLB loaded successfully");

                    // Extract GUID metadata from node extras
                    this.extractGuidMetadata(gltf.scene);

                    // Setup materials and shadows
                    this.setupScene(gltf.scene);

                    resolve(gltf.scene);
                },
                (progress) => {
                    const percent = (progress.loaded / progress.total) * 100;
                    console.log(`Loading: ${percent.toFixed(1)}%`);
                },
                (error) => {
                    console.error("Failed to load GLB:", error);
                    reject(error);
                }
            );
        });
    }

    private extractGuidMetadata(scene: THREE.Group): void {
        let guidCount = 0;

        scene.traverse((node) => {
            if (node instanceof THREE.Mesh) {
                // Check for GUID in userData (from glTF extras)
                const userData = (node as any).userData;
                if (userData && userData.guid) {
                    const guid = userData.guid;
                    const name = userData.name || "Unknown";

                    // Build bidirectional mapping
                    this.guidToMesh.set(guid, node);
                    this.meshToGuid.set(node, guid);

                    // Preload inline metadata to L1 cache
                    this.cacheManager.preloadInline(guid, {
                        guid: guid,
                        category: this.extractCategory(name),
                    });

                    guidCount++;
                }

                // Enable shadow casting
                node.castShadow = true;
                node.receiveShadow = true;
            }
        });

        console.log(`✓ Extracted ${guidCount} GUID mappings`);
    }

    private setupScene(scene: THREE.Group): void {
        scene.traverse((node) => {
            if (node instanceof THREE.Mesh) {
                // Ensure materials are properly configured
                if (node.material) {
                    const materials = Array.isArray(node.material)
                        ? node.material
                        : [node.material];

                    materials.forEach((mat) => {
                        if (mat instanceof THREE.MeshStandardMaterial) {
                            mat.side = THREE.FrontSide;
                            mat.metalness = 0.1;
                            mat.roughness = 0.8;
                        }
                    });
                }
            }
        });
    }

    private extractCategory(name: string): string {
        // Simple category extraction from name
        // In production, this would be more sophisticated
        if (name.includes("Wall")) return "Walls";
        if (name.includes("Floor")) return "Floors";
        if (name.includes("Door")) return "Doors";
        if (name.includes("Window")) return "Windows";
        if (name.includes("Column")) return "Structural Columns";
        return "Unknown";
    }

    getMeshByGuid(guid: string): THREE.Object3D | undefined {
        return this.guidToMesh.get(guid);
    }

    getGuidByMesh(mesh: THREE.Object3D): string | undefined {
        return this.meshToGuid.get(mesh);
    }

    getAllGuids(): string[] {
        return Array.from(this.guidToMesh.keys());
    }

    getStats() {
        return {
            totalElements: this.guidToMesh.size,
        };
    }
}
