import { defineConfig } from "vitepress";

// Grog docs site (VitePress -> GitHub Pages). Same shape as Libation's, which this is modeled on.
// https://vitepress.dev/reference/site-config
export default defineConfig({
  vite: {
    // Never watch the C# solution: VS lock files (.vsidx) EBUSY the dev server otherwise.
    server: {
      watch: {
        ignored: ["**/src/**", "**/tests/**", "**/tools/**", "**/.vs/**", "**/bin/**", "**/obj/**", "**/_build-cache/**"],
      },
    },
  },
  title: "Grog",
  description: "Grog - a DRM-free GOG.com library backup tool for Windows, Linux and macOS",
  head: [["link", { rel: "icon", href: "/favicon.ico" }]],
  cleanUrls: true,
  // Only these paths become pages; the C# tree stays out of the site build.
  srcExclude: ["README.md", "CONTRIBUTING.md", "SECURITY.md", "src/**", "tests/**", "tools/**", "_to_delete/**", ".github/**", "_docs/**"],
  themeConfig: {
    logo: {
      light: "/grog-amber.png",
      dark: "/grog-amber.png",
    },

    footer: {
      message: "Released under the GPL-3.0-or-later License",
    },

    editLink: {
      pattern: "https://github.com/KGScottCode/Grog/edit/master/:path",
    },

    lastUpdated: true,

    nav: [
      { text: "FAQ", link: "/docs/FAQ" },
      { text: "Download", link: "https://github.com/KGScottCode/Grog/releases/latest" },
      { text: "Issues & Requests", link: "https://github.com/KGScottCode/Grog/issues" },
    ],
    sidebar: [
      {
        items: [
          { text: "Overview", link: "/" },
          { text: "FAQ", link: "/docs/FAQ" },
          { text: "Issues & Requests", link: "https://github.com/KGScottCode/Grog/issues" },
        ],
      },
      {
        text: "Platforms",
        collapsed: false,
        items: [
          { text: "Windows", link: "/docs/install/Windows" },
          { text: "macOS", link: "/docs/install/macOS" },
          { text: "Linux", link: "/docs/install/Linux" },
        ],
      },
      {
        text: "Advanced",
        collapsed: false,
        items: [
          { text: "Command line", link: "/docs/CLI" },
        ],
      },
    ],

    outline: {
      level: "deep",
    },

    socialLinks: [{ icon: "github", link: "https://github.com/KGScottCode/Grog" }],

    search: {
      provider: "local",
    },

    externalLinkIcon: true,
  },
});
