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
                },
            );
        });
    }

    private extractGuidMetadata(scene: THREE.Group): void {
        let guidCount = 0;

        scene.traverse((node) => {
            const userData = (node as any).userData;
            if (userData && userData.guid) {
                const guid = userData.guid;
                const name = userData.name || "Unknown";
                this.guidToMesh.set(guid, node);
                this.meshToGuid.set(node, guid);
                this.cacheManager.preloadInline(guid, {
                    guid: guid,
                    category: this.extractCategory(name),
                });
                guidCount++;
            }
            if (node instanceof THREE.Mesh) {
                node.castShadow = true;
                node.receiveShadow = true;
            }
        });

        console.log(`✓ Extracted ${guidCount} GUID mappings`);
    }

    private setupScene(scene: THREE.Group): void {
        const defaultMaterial = new THREE.MeshStandardMaterial({
            color: 0xd8d8d8,
            metalness: 0.12,
            roughness: 0.6,
            side: THREE.DoubleSide,
        });
        const color = new THREE.Color();

        scene.traverse((node) => {
            if (node instanceof THREE.Mesh) {
                const colorAttr = (node.geometry as any)?.attributes?.color as THREE.BufferAttribute | undefined;
                const hasVertexColors = !!colorAttr;
                if (colorAttr && !(colorAttr as any)._srgbToLinear) {
                    for (let i = 0; i < colorAttr.count; i++) {
                        color.setRGB(colorAttr.getX(i), colorAttr.getY(i), colorAttr.getZ(i), THREE.SRGBColorSpace);
                        colorAttr.setXYZ(i, color.r, color.g, color.b);
                    }
                    (colorAttr as any)._srgbToLinear = true;
                    colorAttr.needsUpdate = true;
                }
                const materials = node.material
                    ? Array.isArray(node.material)
                        ? node.material
                        : [node.material]
                    : [];

                let needsDefault = materials.length === 0;
                materials.forEach((mat) => {
                    if (mat instanceof THREE.MeshStandardMaterial) {
                        mat.side = THREE.FrontSide;
                        if (hasVertexColors) {
                            mat.vertexColors = true;
                            if (mat.color.getHex() === 0x000000) {
                                mat.color.setHex(0xffffff);
                            }
                        } else if (mat.color.getHex() === 0x000000) {
                            mat.color.setHex(0xd8d8d8);
                        }
                    } else if (mat instanceof THREE.MeshBasicMaterial) {
                        const std = new THREE.MeshStandardMaterial({
                            color: mat.color.clone(),
                            map: mat.map,
                            side: THREE.FrontSide,
                            metalness: 0.12,
                            roughness: 0.65,
                        });
                        if (hasVertexColors || mat.vertexColors) {
                            std.vertexColors = true;
                            if (std.color.getHex() === 0x000000) {
                                std.color.setHex(0xffffff);
                            }
                        }
                        (node as THREE.Mesh).material = std;
                    } else {
                        needsDefault = true;
                    }
                });

                if (needsDefault) {
                    if (hasVertexColors) {
                        const mat = defaultMaterial.clone();
                        mat.vertexColors = true;
                        mat.color.setHex(0xffffff);
                        node.material = mat;
                    } else {
                        node.material = defaultMaterial;
                    }
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
