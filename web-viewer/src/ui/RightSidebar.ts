import { ElementMetadata } from "../core/CacheSystem";

/**
 * Right Sidebar - Metadata Panel
 * Displays Revit parameters when element is selected
 */
export class RightSidebar {
    private container: HTMLElement;

    constructor(containerId: string) {
        this.container = document.getElementById(containerId)!;
    }

    showMetadata(metadata: ElementMetadata, latency: number): void {
        this.container.classList.remove("empty");

        const html = `
      <div class="metadata-header">
        <h3>${this.escapeHtml(metadata.typeName || metadata.category || "Unknown")}</h3>
        <div class="category">${this.escapeHtml(metadata.category || "N/A")}</div>
      </div>

      <div class="param-group">
        <div class="param-group-title">기본 정보</div>
        <div class="param-row">
          <span class="param-name">GUID</span>
          <span class="param-value">${this.truncateGuid(metadata.guid)}</span>
        </div>
        <div class="param-row">
          <span class="param-name">Element ID</span>
          <span class="param-value">${metadata.elementId}</span>
        </div>
        ${
            metadata.familyName
                ? `
        <div class="param-row">
          <span class="param-name">Family</span>
          <span class="param-value">${this.escapeHtml(metadata.familyName)}</span>
        </div>
        `
                : ""
        }
        ${
            metadata.typeName
                ? `
        <div class="param-row">
          <span class="param-name">Type</span>
          <span class="param-value">${this.escapeHtml(metadata.typeName)}</span>
        </div>
        `
                : ""
        }
        ${
            metadata.levelName
                ? `
        <div class="param-row">
          <span class="param-name">Level</span>
          <span class="param-value">${this.escapeHtml(metadata.levelName)}</span>
        </div>
        `
                : ""
        }
        ${
            metadata.phaseName
                ? `
        <div class="param-row">
          <span class="param-name">Phase</span>
          <span class="param-value">${this.escapeHtml(metadata.phaseName)}</span>
        </div>
        `
                : ""
        }
      </div>

      ${
          metadata.volume || metadata.area
              ? `
      <div class="param-group">
        <div class="param-group-title">수량 정보</div>
        ${
            metadata.volume
                ? `
        <div class="param-row">
          <span class="param-name">Volume</span>
          <span class="param-value">${this.formatNumber(metadata.volume)} ft³</span>
        </div>
        `
                : ""
        }
        ${
            metadata.area
                ? `
        <div class="param-row">
          <span class="param-name">Area</span>
          <span class="param-value">${this.formatNumber(metadata.area)} ft²</span>
        </div>
        `
                : ""
        }
      </div>
      `
              : ""
      }

      ${
          metadata.instanceParameters && Object.keys(metadata.instanceParameters).length > 0
              ? `
      <div class="param-group">
        <div class="param-group-title">Instance Parameters</div>
        ${this.renderParameters(metadata.instanceParameters)}
      </div>
      `
              : ""
      }

      ${
          metadata.typeParameters && Object.keys(metadata.typeParameters).length > 0
              ? `
      <div class="param-group">
        <div class="param-group-title">Type Parameters</div>
        ${this.renderParameters(metadata.typeParameters)}
      </div>
      `
              : ""
      }

      <div class="param-group">
        <div class="param-group-title">Performance</div>
        <div class="param-row">
          <span class="param-name">Query Latency</span>
          <span class="param-value">${latency.toFixed(1)} ms</span>
        </div>
      </div>
    `;

        this.container.innerHTML = html;
    }

    clear(): void {
        this.container.classList.add("empty");
        this.container.innerHTML =
            "<p>3D 모델에서 객체를 클릭하면<br>Revit 파라미터가 표시됩니다</p>";
    }

    private renderParameters(params: Record<string, any>): string {
        return Object.entries(params)
            .slice(0, 10) // Limit to first 10 parameters
            .map(([key, value]) => {
                const displayValue = typeof value === "object" ? value.displayValue : value;
                return `
          <div class="param-row">
            <span class="param-name">${this.escapeHtml(key)}</span>
            <span class="param-value">${this.escapeHtml(String(displayValue || "N/A"))}</span>
          </div>
        `;
            })
            .join("");
    }

    private truncateGuid(guid: string): string {
        return guid.substring(0, 8) + "...";
    }

    private formatNumber(num: number): string {
        return num.toFixed(2);
    }

    private escapeHtml(text: string): string {
        const div = document.createElement("div");
        div.textContent = text;
        return div.innerHTML;
    }
}
