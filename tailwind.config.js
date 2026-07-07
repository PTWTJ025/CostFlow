/** @type {import('tailwindcss').Config} */
module.exports = {
  darkMode: "class",
  content: [
    "./Views/**/*.cshtml",
    "./wwwroot/js/**/*.js"
  ],
  theme: {
    extend: {
      colors: {
        "secondary": "#505f76",
        "on-error": "#ffffff",
        "primary": "#00288e",
        "tertiary-fixed": "#ffdbce",
        "on-tertiary-fixed": "#380d00",
        "on-tertiary-container": "#ffa583",
        "outline-variant": "#c4c5d5",
        "primary-fixed-dim": "#b8c4ff",
        "inverse-on-surface": "#f1f0fa",
        "error-container": "#ffdad6",
        "surface-bright": "#fbf8ff",
        "error": "#ba1a1a",
        "surface-container-high": "#e8e7f1",
        "inverse-primary": "#b8c4ff",
        "surface": "#fbf8ff",
        "inverse-surface": "#2f3037",
        "primary-container": "#1e40af",
        "surface-container-lowest": "#ffffff",
        "secondary-fixed-dim": "#b7c8e1",
        "on-primary-fixed-variant": "#173bab",
        "surface-container-low": "#f4f2fc",
        "on-error-container": "#93000a",
        "surface-tint": "#3755c3",
        "tertiary-container": "#872d00",
        "outline": "#757684",
        "surface-dim": "#dad9e3",
        "on-secondary-fixed": "#0b1c30",
        "secondary-container": "#d0e1fb",
        "on-tertiary": "#ffffff",
        "on-secondary-container": "#54647a",
        "on-primary-container": "#a8b8ff",
        "on-surface": "#1a1b22",
        "tertiary": "#611e00",
        "secondary-fixed": "#d3e4fe",
        "surface-container-highest": "#e3e1eb",
        "on-surface-variant": "#444653",
        "surface-variant": "#e3e1eb",
        "tertiary-fixed-dim": "#ffb59a",
        "on-tertiary-fixed-variant": "#802a00",
        "surface-container": "#eeedf7",
        "on-secondary": "#ffffff",
        "on-primary": "#ffffff",
        "on-secondary-fixed-variant": "#38485d",
        "on-background": "#1a1b22",
        "primary-fixed": "#dde1ff",
        "background": "#fbf8ff",
        "on-primary-fixed": "#001453"
      },
      borderRadius: {
        "DEFAULT": "4px",
        "sm": "2px",
        "md": "6px",
        "lg": "8px",
        "xl": "12px",
        "full": "9999px"
      },
      spacing: {
        "lg": "24px",
        "md": "16px",
        "xl": "32px",
        "base": "4px",
        "sm": "8px",
        "xs": "4px",
        "container-max": "1440px",
        "gutter": "20px"
      },
      fontFamily: {
        "headline-md": ["Ubuntu", "IBM Plex Sans Thai", "IBM Plex Sans", "ui-sans-serif", "system-ui", "sans-serif"],
        "label-bold": ["Ubuntu", "IBM Plex Sans Thai", "IBM Plex Sans", "ui-sans-serif", "system-ui", "sans-serif"],
        "body-md": ["Ubuntu", "IBM Plex Sans Thai", "IBM Plex Sans", "ui-sans-serif", "system-ui", "sans-serif"],
        "body-lg": ["Ubuntu", "IBM Plex Sans Thai", "IBM Plex Sans", "ui-sans-serif", "system-ui", "sans-serif"],
        "headline-sm": ["Ubuntu", "IBM Plex Sans Thai", "IBM Plex Sans", "ui-sans-serif", "system-ui", "sans-serif"],
        "label-sm": ["Ubuntu", "IBM Plex Sans Thai", "IBM Plex Sans", "ui-sans-serif", "system-ui", "sans-serif"],
        "data-tabular": ["Ubuntu", "IBM Plex Sans Thai", "IBM Plex Sans", "ui-sans-serif", "system-ui", "sans-serif"],
        "headline-lg": ["Ubuntu", "IBM Plex Sans Thai", "IBM Plex Sans", "ui-sans-serif", "system-ui", "sans-serif"],
        "sans": ["Ubuntu", "IBM Plex Sans Thai", "IBM Plex Sans", "ui-sans-serif", "system-ui", "sans-serif"]
      },
      fontSize: {
        "headline-md": ["24px", { lineHeight: "32px", letterSpacing: "-0.01em", fontWeight: "600" }],
        "label-bold": ["12px", { lineHeight: "16px", letterSpacing: "0.05em", fontWeight: "600" }],
        "body-md": ["14px", { lineHeight: "20px", fontWeight: "400" }],
        "body-lg": ["16px", { lineHeight: "24px", fontWeight: "400" }],
        "headline-sm": ["20px", { lineHeight: "28px", fontWeight: "600" }],
        "label-sm": ["12px", { lineHeight: "16px", fontWeight: "400" }],
        "data-tabular": ["14px", { lineHeight: "20px", fontWeight: "500" }],
        "headline-lg": ["32px", { lineHeight: "40px", letterSpacing: "-0.02em", fontWeight: "700" }]
      }
    }
  },
  plugins: [
    require("@tailwindcss/forms"),
    require("@tailwindcss/container-queries")
  ]
};
