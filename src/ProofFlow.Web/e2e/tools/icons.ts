import { chromium } from '@playwright/test';
import { readFile, writeFile } from 'node:fs/promises';
import { resolve } from 'node:path';

/**
 * Renders wwwroot/favicon.svg into the raster sizes browsers and app installers still want.
 *
 * Generated from the one source rather than hand-drawn per size, so the mark cannot drift between
 * the tab icon and the installed app icon. Re-run after changing favicon.svg:
 *
 *   npx tsx e2e/tools/icons.ts
 */

const ROOT = resolve(import.meta.dirname, '../../wwwroot');
const SIZES = [
  { file: 'icon-192.png', size: 192, pad: 0 },
  { file: 'icon-512.png', size: 512, pad: 0 },
  // iOS composites the icon onto an opaque tile and applies its own rounding, so it needs the
  // artwork inset — otherwise the corners of the mark get clipped away.
  { file: 'apple-touch-icon.png', size: 180, pad: 20 },
  { file: 'favicon-32.png', size: 32, pad: 0 },
];

async function main(): Promise<void> {
  const svg = await readFile(resolve(ROOT, 'favicon.svg'), 'utf8');
  const browser = await chromium.launch();

  for (const { file, size, pad } of SIZES) {
    const page = await browser.newPage({
      viewport: { width: size, height: size },
      deviceScaleFactor: 1,
    });

    await page.setContent(
      `<!doctype html><style>
         html,body{margin:0;padding:0;background:transparent}
         div{width:${size}px;height:${size}px;padding:${pad}px;box-sizing:border-box}
         svg{width:100%;height:100%;display:block}
       </style><div>${svg}</div>`,
      { waitUntil: 'load' },
    );

    const shot = await page.screenshot({ omitBackground: true });
    await writeFile(resolve(ROOT, file), shot);
    console.log(`${file} (${size}px)`);
    await page.close();
  }

  await browser.close();
}

main().catch((error) => {
  console.error(error);
  process.exit(1);
});
