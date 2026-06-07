/** @type {import('next').NextConfig} */
const nextConfig = {
  output: 'export', // Wichtig für den statischen Export!
  images: {
    unoptimized: true, // Notwendig für den statischen Export
  }
};

export default nextConfig;