/**
 * Plugin Routes
 * API endpoints for plugin management
 */

const express = require('express');
const router = express.Router();

/**
 * GET /api/plugins
 * Get all plugins
 */
router.get('/', (req, res) => {
  try {
    const plugins = req.pluginManager.getAllPlugins();
    res.json({
      success: true,
      count: plugins.length,
      plugins
    });
  } catch (error) {
    res.status(500).json({
      success: false,
      error: error.message
    });
  }
});

/**
 * GET /api/plugins/frontend
 * Get plugins formatted for frontend consumption
 */
router.get('/frontend', (req, res) => {
  try {
    const plugins = req.pluginManager.getFrontendPlugins();
    res.json({
      success: true,
      plugins
    });
  } catch (error) {
    res.status(500).json({
      success: false,
      error: error.message
    });
  }
});

/**
 * GET /api/plugins/:id
 * Get a specific plugin
 */
router.get('/:id', (req, res) => {
  try {
    const plugin = req.pluginManager.getPlugin(req.params.id);
    
    if (!plugin) {
      return res.status(404).json({
        success: false,
        error: 'Plugin not found'
      });
    }
    
    res.json({
      success: true,
      plugin
    });
  } catch (error) {
    res.status(500).json({
      success: false,
      error: error.message
    });
  }
});

/**
 * POST /api/plugins/:id/toggle
 * Enable/disable a plugin
 */
router.post('/:id/toggle', (req, res) => {
  try {
    const { enabled } = req.body;
    const plugin = req.pluginManager.togglePlugin(req.params.id, enabled);
    
    res.json({
      success: true,
      message: `Plugin ${enabled ? 'enabled' : 'disabled'}`,
      plugin
    });
  } catch (error) {
    res.status(500).json({
      success: false,
      error: error.message
    });
  }
});

/**
 * POST /api/plugins/reload
 * Reload all plugins
 */
router.post('/reload', async (req, res) => {
  try {
    await req.pluginManager.loadPlugins();
    const plugins = req.pluginManager.getAllPlugins();
    
    res.json({
      success: true,
      message: 'Plugins reloaded',
      count: plugins.length,
      plugins
    });
  } catch (error) {
    res.status(500).json({
      success: false,
      error: error.message
    });
  }
});

/**
 * DELETE /api/plugins/:id
 * Unload a plugin
 */
router.delete('/:id', async (req, res) => {
  try {
    await req.pluginManager.unloadPlugin(req.params.id);
    
    res.json({
      success: true,
      message: 'Plugin unloaded'
    });
  } catch (error) {
    res.status(500).json({
      success: false,
      error: error.message
    });
  }
});

/**
 * POST /api/plugins/:id/execute
 * Execute a plugin hook
 */
router.post('/:id/execute', async (req, res) => {
  try {
    const { hook, args = [] } = req.body;
    const plugin = req.pluginManager.getPlugin(req.params.id);
    
    if (!plugin) {
      return res.status(404).json({
        success: false,
        error: 'Plugin not found'
      });
    }
    
    if (!plugin.hooks[hook]) {
      return res.status(404).json({
        success: false,
        error: `Hook '${hook}' not found in plugin`
      });
    }
    
    const result = await plugin.hooks[hook](...args);
    
    res.json({
      success: true,
      result
    });
  } catch (error) {
    res.status(500).json({
      success: false,
      error: error.message
    });
  }
});

module.exports = router;
