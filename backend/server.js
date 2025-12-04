/**
 * CollabHub Backend Server
 * Main entry point for the plugin-based collaboration platform
 */

const express = require('express');
const cors = require('cors');
const bodyParser = require('body-parser');
const dotenv = require('dotenv');
const pluginRoutes = require('./routes/pluginRoutes');
const PluginManager = require('./services/PluginManager');

// Load environment variables
dotenv.config();

const app = express();
const PORT = process.env.PORT || 5000;

// Middleware
app.use(cors());
app.use(bodyParser.json());
app.use(bodyParser.urlencoded({ extended: true }));

// Initialize Plugin Manager
const pluginManager = new PluginManager();

// Make plugin manager available to routes
app.use((req, res, next) => {
  req.pluginManager = pluginManager;
  next();
});

// Routes
app.use('/api/plugins', pluginRoutes);

// Health check endpoint
app.get('/api/health', (req, res) => {
  res.json({ 
    status: 'ok', 
    message: 'CollabHub Backend is running',
    timestamp: new Date().toISOString()
  });
});

// Error handling middleware
app.use((err, req, res, next) => {
  console.error('Error:', err);
  res.status(err.status || 500).json({
    error: {
      message: err.message || 'Internal Server Error',
      status: err.status || 500
    }
  });
});

// Start server
app.listen(PORT, async () => {
  console.log(`🚀 CollabHub Backend running on port ${PORT}`);
  console.log(`📦 Environment: ${process.env.NODE_ENV}`);
  
  // Load plugins on startup
  try {
    await pluginManager.loadPlugins();
    console.log(`✅ Loaded ${pluginManager.getPluginCount()} plugins`);
  } catch (error) {
    console.error('❌ Error loading plugins:', error.message);
  }
});

module.exports = app;
