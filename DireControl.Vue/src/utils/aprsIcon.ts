import L from "leaflet";

/**
 * APRS symbol sprite sheets: 16 columns x 6 rows of 24x24 icons.
 * Symbol codes range from ASCII 33 (!) to ASCII 126 (~) = 94 positions.
 * Grid position: col = (code - 33) % 16, row = floor((code - 33) / 16).
 */
const SYMBOL_SIZE = 24;
const COLS = 16;

const PRIMARY_SHEET = "/aprs-symbols-24-0.png";
const ALTERNATE_SHEET = "/aprs-symbols-24-1.png";
const OVERLAY_SHEET = "/aprs-symbols-24-2.png";

function spriteOffset(symbolCode: string): { x: number; y: number } | null {
  const code = symbolCode.charCodeAt(0);
  if (code < 33 || code > 126) return null;
  const index = code - 33;
  return {
    x: (index % COLS) * SYMBOL_SIZE,
    y: Math.floor(index / COLS) * SYMBOL_SIZE,
  };
}

function isOverlayChar(ch: string): boolean {
  return (ch >= "0" && ch <= "9") || (ch >= "A" && ch <= "Z");
}

/**
 * Returns a Leaflet DivIcon for the given APRS symbol.
 *
 * @param symbolTable - The table character: "/" for primary, "\" for alternate,
 *   or an overlay character (0-9, A-Z) which implies alternate table with overlay.
 * @param symbolCode - The symbol code character (ASCII 33-126).
 * @param heading - Optional heading in degrees. When non-null, the icon is
 *   wrapped in a rotating container and a directional chevron is added.
 * @param isWeatherStation - When true, adds a dual pulsing teal ring and "W" badge.
 * @param opacity - Optional opacity override (0–1).
 * @param showChevron - Set false to suppress the heading chevron (e.g. ghost markers).
 */
export function createAprsIcon(
  symbolTable: string,
  symbolCode: string,
  heading?: number | null,
  isWeatherStation?: boolean,
  opacity?: number,
  showChevron = true,
): L.DivIcon {
  const offset = spriteOffset(symbolCode);
  if (!offset) return fallbackIcon();

  const isPrimary = symbolTable === "/";
  const sheet = isPrimary ? PRIMARY_SHEET : ALTERNATE_SHEET;

  // Determine overlay character: for alternate table, the table char itself
  // may be an overlay character (0-9, A-Z) instead of plain "\"
  const overlayChar =
    !isPrimary && symbolTable !== "\\" && isOverlayChar(symbolTable)
      ? symbolTable
      : null;

  const opacityStyle = opacity != null ? `opacity:${opacity};` : "";

  let overlayHtml = "";
  if (overlayChar) {
    // Find the overlay character position in the overlay sheet
    const overlayOffset = overlayCharOffset(overlayChar);
    if (overlayOffset) {
      overlayHtml = `<div style="position:absolute;top:0;left:0;width:${SYMBOL_SIZE}px;height:${SYMBOL_SIZE}px;background:url('${OVERLAY_SHEET}') -${overlayOffset.x}px -${overlayOffset.y}px;pointer-events:none;"></div>`;
    }
  }

  // Dual pulsing rings + "W" badge for weather stations
  const ringHtml = isWeatherStation
    ? `<div class="wx-ring wx-ring-1"></div><div class="wx-ring wx-ring-2"></div><div class="wx-badge-w">W</div>`
    : "";

  // The inner aprs-icon div holds the sprite, rings, and any overlay
  const aprsIconDiv = `<div class="aprs-icon" style="width:${SYMBOL_SIZE}px;height:${SYMBOL_SIZE}px;position:relative;${opacityStyle}">${ringHtml}<div style="width:${SYMBOL_SIZE}px;height:${SYMBOL_SIZE}px;background:url('${sheet}') -${offset.x}px -${offset.y}px;"></div>${overlayHtml}</div>`;

  const hasHeading = heading != null && heading >= 0;

  let html: string;
  if (hasHeading) {
    // Wrap in a rotating container so the chevron stays outside aprs-icon's
    // coordinate system and rotation applies to both icon and chevron together.
    // CSS transition on .aprs-heading-wrapper enables smooth heading updates
    // when the transform is updated in-place via the DOM.
    const chevronHtml =
      showChevron
        ? `<div class="aprs-chevron"></div>`
        : "";
    html = `<div class="aprs-heading-wrapper" style="transform:rotate(${heading}deg);">${aprsIconDiv}${chevronHtml}</div>`;
  } else {
    html = aprsIconDiv;
  }

  return L.divIcon({
    html,
    className: "aprs-icon-container",
    iconSize: [SYMBOL_SIZE, SYMBOL_SIZE],
    iconAnchor: [SYMBOL_SIZE / 2, SYMBOL_SIZE / 2],
    popupAnchor: [0, -SYMBOL_SIZE / 2],
  });
}

/**
 * The overlay character sheet (table 2) uses the same 16-col grid layout.
 * Digits 0-9 start at ASCII 48, letters A-Z at ASCII 65.
 * In the overlay sheet, '0' is at row 0 col 0, '1' at row 0 col 1, etc.
 * 'A' starts at row 1 col 0, 'B' at row 1 col 1, etc.
 */
function overlayCharOffset(ch: string): { x: number; y: number } | null {
  const code = ch.charCodeAt(0);
  if (code >= 48 && code <= 57) {
    // '0'-'9' → positions 0-9 in overlay sheet row 0
    // But '0' maps to index 15 in the original spec (after '1'-'9')
    // Looking at the sprite: row 0 has "1 2 3 4 5 6 7 8 9" then row 1 has "0 A B C..."
    // Actually from the image: row 0 = empty + "1 2 3 4 5 6 7 8 9", row 1 = "A B C D..."
    // Let me map based on the actual sprite layout observed:
    // Row 0: pos 0=empty, 1="1", 2="2", ..., 9="9"
    // Row 1: pos 0="0"(?), or pos 0="A"
    // From the image: Row 0 has "1 2 3 4 5 6 7 8 9" starting at col 1
    //                 Row 1 has "A B C D E F G H I J K L M N O P"
    //                 Row 2 has "Q R S T U V W X Y Z"
    if (ch === "0") {
      // '0' is at row 0 col 0 in the overlay sheet
      return { x: 0, y: 0 };
    }
    // '1'-'9' at row 0, cols 1-9
    const digit = code - 48;
    return { x: digit * SYMBOL_SIZE, y: 0 };
  }
  if (code >= 65 && code <= 90) {
    // 'A'-'Z' → 26 chars starting at row 1 col 0
    const letterIndex = code - 65;
    const col = letterIndex % COLS;
    const row = 1 + Math.floor(letterIndex / COLS);
    return { x: col * SYMBOL_SIZE, y: row * SYMBOL_SIZE };
  }
  return null;
}

function fallbackIcon(): L.DivIcon {
  return L.divIcon({
    html: `<div style="width:${SYMBOL_SIZE}px;height:${SYMBOL_SIZE}px;background:#666;border:2px solid #fff;border-radius:50%;box-sizing:border-box;"></div>`,
    className: "aprs-icon-container",
    iconSize: [SYMBOL_SIZE, SYMBOL_SIZE],
    iconAnchor: [SYMBOL_SIZE / 2, SYMBOL_SIZE / 2],
    popupAnchor: [0, -SYMBOL_SIZE / 2],
  });
}

/**
 * Parse an APRS 2-character symbol string into table and code.
 * The Symbol field from the backend is 2 chars: [table][code].
 */
export function parseAprsSymbol(symbol: string): {
  table: string;
  code: string;
} {
  if (symbol.length >= 2) {
    return { table: symbol[0]!, code: symbol[1]! };
  }
  return { table: "/", code: "/" }; // fallback: primary table, dot
}

/**
 * Returns CSS style properties for rendering an APRS symbol in a Vue template
 * (as opposed to a Leaflet DivIcon). Useful for inline <div> icons in UI components.
 */
export function getSymbolStyle(
  symbolTable: string,
  symbolCode: string,
): { backgroundImage: string; backgroundPosition: string; width: string; height: string; display: string } {
  const offset = spriteOffset(symbolCode)
  if (!offset) {
    return {
      backgroundImage: 'none',
      backgroundPosition: '0 0',
      width: `${SYMBOL_SIZE}px`,
      height: `${SYMBOL_SIZE}px`,
      display: 'inline-block',
    }
  }
  const isPrimary = symbolTable === '/'
  const sheet = isPrimary ? PRIMARY_SHEET : ALTERNATE_SHEET
  return {
    backgroundImage: `url('${sheet}')`,
    backgroundPosition: `-${offset.x}px -${offset.y}px`,
    width: `${SYMBOL_SIZE}px`,
    height: `${SYMBOL_SIZE}px`,
    display: 'inline-block',
  }
}
